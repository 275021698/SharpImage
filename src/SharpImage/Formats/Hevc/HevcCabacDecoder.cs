using System;
using System.Runtime.CompilerServices;

namespace SharpImage.Formats.Hevc
{
    /// <summary>
    /// CABAC (Context-Adaptive Binary Arithmetic Coding) decoder for HEVC/H.265.
    /// Uses the same 64-state arithmetic engine as H.264 but with HEVC-specific contexts.
    /// </summary>
    /// <remarks>
    /// HEVC CABAC is based on ITU-T H.265 Section 9.3.
    /// Key differences from H.264:
    /// - Different context initialization values (m, n pairs)
    /// - Different context index assignments for syntax elements
    /// - Additional contexts for new HEVC features (SAO, transform skip, etc.)
    /// - Dependent slice context state persistence
    /// </remarks>
    public ref struct HevcCabacDecoder
    {
        private readonly ReadOnlySpan<byte> data;

        // The arithmetic decoder of 9.3.4.3 with the bitstream in a window: 'value' holds ivlOffset (9 bits) followed
        // by 'k' look-ahead bits, so ivlOffset = value >> k and comparisons scale ivlCurrRange by 2^k; renormalizing
        // just lowers k. 'pos' is the next byte to load (counting zero bytes past the end, which is what the spec
        // engine reads there); the spec's bit position is pos * 8 - k.
        private ulong value;
        private int k;
        private int pos;
        private uint ivlCurrRange;  // HEVC naming (was codIRange in H.264)

        /// <summary>
        /// Context states: probability state (0-63) and MPS value (0 or 1).
        /// Stored as: (stateIdx << 1) | valMps
        /// </summary>
        private readonly Span<byte> contextStates;

        /// <summary>
        /// Gets whether the decoder has reached the end of data.
        /// </summary>
        public readonly bool IsEndOfStream => false;   // (the spec engine reads zeros past the end; slices end by their flag)

        /// <summary>
        /// Creates a new HEVC CABAC decoder.
        /// </summary>
        /// <param name="bitstreamData">The CABAC-encoded bitstream data (RBSP).</param>
        /// <param name="contextStateStorage">Pre-allocated storage for context states.</param>
        /// <param name="sliceQp">Slice quantization parameter for context initialization.</param>
        /// <param name="initType">Initialization type: 0 for I slice, 1 for P slice, 2 for B slice.</param>
        public HevcCabacDecoder(ReadOnlySpan<byte> bitstreamData, Span<byte> contextStateStorage,
            int sliceQp, int initType)
        {
            if (contextStateStorage.Length < HevcCabacContextIndex.TotalContexts)
                throw new ArgumentException(
                    $"Context storage must have at least {HevcCabacContextIndex.TotalContexts} bytes",
                    nameof(contextStateStorage));

            data = bitstreamData;
            contextStates = contextStateStorage;
            value = 0;
            k = 0;
            pos = 0;
            ivlCurrRange = 510;

            // Initialize arithmetic decoder state (9.3.2.2): the first 9 bits into ivlOffset
            InitEngine(0);

            // Initialize all context states
            InitializeContexts(sliceQp, initType);
        }

        /// <summary>
        /// Creates a decoder that inherits context state from a previous slice.
        /// Used for dependent slices in HEVC.
        /// </summary>
        public HevcCabacDecoder(ReadOnlySpan<byte> bitstreamData, Span<byte> sharedContextStates)
        {
            data = bitstreamData;
            contextStates = sharedContextStates;
            value = 0;
            k = 0;
            pos = 0;
            ivlCurrRange = 510;
            InitEngine(0);
            // Context states are already initialized from parent slice
        }

        /// <summary>
        /// Initializes all context states based on slice QP and initialization type.
        /// Matches FFmpeg's cabac_init_state exactly.
        /// </summary>
        public void InitializeContexts(int sliceQp, int initType)
        {
            ReadOnlySpan<byte> initValues = HevcCabacTables.GetInitValues(initType);
            int count = Math.Min(HevcCabacContextIndex.TotalContexts, initValues.Length);

            for (int i = 0; i < count; i++)
                contextStates[i] = HevcCabacTables.ComputeInitState(initValues[i], sliceQp);

            // Zero any remaining states
            for (int i = count; i < contextStates.Length; i++)
                contextStates[i] = 0;
        }

        /// <summary>
        /// Decodes a single binary decision using the specified context (9.3.4.3.2).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeBin(int contextIndex)
        {
            byte state = contextStates[contextIndex];
            int stateIdx = state >> 1;
            int valMps = state & 1;
            if (DiagBinLog != null) DiagLog('C', contextIndex, state);

            uint lps = HevcCabacTables.RangeLps[(stateIdx << 2) + (int)((ivlCurrRange >> 6) & 3)];
            ivlCurrRange -= lps;
            ulong scaled = (ulong)ivlCurrRange << k;
            int binVal;
            if (value < scaled)
            {
                // MPS
                binVal = valMps;
                stateIdx = HevcCabacTables.TransitionIndexMps[stateIdx];
            }
            else
            {
                // LPS
                binVal = 1 - valMps;
                value -= scaled;
                ivlCurrRange = lps;
                if (stateIdx == 0)
                    valMps = 1 - valMps;
                stateIdx = HevcCabacTables.TransitionIndexLps[stateIdx];
            }

            contextStates[contextIndex] = (byte)((stateIdx << 1) | valMps);
            Renormalize();
            DiagBinCount++;
            return binVal;
        }

        /// <summary>
        /// Decodes a binary decision in bypass mode (9.3.4.3.4).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeBypass()
        {
            if (DiagBinLog != null) DiagLog('B', -1, 0);
            k--;
            ulong scaled = (ulong)ivlCurrRange << k;
            int result = 0;
            if (value >= scaled)
            {
                value -= scaled;
                result = 1;
            }

            if (k < 8) Refill();
            DiagBinCount++;
            return result;
        }

        /// <summary>
        /// Decodes a terminate bin (9.3.4.3.5).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeTerminate()
        {
            if (DiagBinLog != null) DiagLog('T', -1, 0);
            ivlCurrRange -= 2;
            if (value >= (ulong)ivlCurrRange << k)
            {
                DiagBinCount++;
                return 1;
            }

            Renormalize();
            DiagBinCount++;
            return 0;
        }

        /// <summary>
        /// Decodes multiple bypass bins as an unsigned integer.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint DecodeBypassBits(int numberOfBits)
        {
            uint value = 0;
            for (int i = 0; i < numberOfBits; i++)
            {
                value = (value << 1) | (uint)DecodeBypass();
            }
            return value;
        }

        /// <summary>
        /// Decodes a sign flag using bypass mode.
        /// Returns the signed version of the absolute value.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int DecodeSign(uint absoluteValue)
        {
            if (absoluteValue == 0)
                return 0;
            return DecodeBypass() != 0 ? -(int)absoluteValue : (int)absoluteValue;
        }

        /// <summary>
        /// Decodes a truncated Rice (TR) binarized syntax element.
        /// Used for coeff_abs_level_remaining in HEVC.
        /// </summary>
        /// <param name="riceParameter">Rice parameter (cRiceParam).</param>
        /// <param name="truncationValue">Maximum prefix length.</param>
        public uint DecodeTruncatedRice(int riceParameter, int truncationValue)
        {
            // Count prefix zeros
            int prefix = 0;
            while (prefix < truncationValue && DecodeBypass() == 0)
            {
                prefix++;
            }

            if (prefix < truncationValue)
            {
                // Suffix exists: read riceParameter bypass bins
                uint suffix = DecodeBypassBits(riceParameter);
                return (uint)((prefix << riceParameter) + suffix);
            }
            else
            {
                // Exp-Golomb suffix for large values
                return DecodeExpGolombBypass((uint)riceParameter) + (uint)(truncationValue << riceParameter);
            }
        }

        /// <summary>
        /// Decodes an exp-Golomb coded value using bypass bins.
        /// Used as suffix in truncated Rice coding.
        /// </summary>
        private uint DecodeExpGolombBypass(uint k)
        {
            // k-th order exp-Golomb
            int leadingZeros = 0;
            while (DecodeBypass() == 0 && leadingZeros < 32)
            {
                leadingZeros++;
            }

            uint value = (1u << leadingZeros) - 1;
            uint suffix = 0;
            for (int i = 0; i < leadingZeros + (int)k; i++)
            {
                suffix = (suffix << 1) | (uint)DecodeBypass();
            }

            return value + (suffix >> (int)k);
        }

        /// <summary>
        /// Decodes a unary-coded syntax element with max value.
        /// </summary>
        public int DecodeUnary(int maxValue, int contextOffset)
        {
            int value = 0;
            while (value < maxValue && DecodeBin(contextOffset) != 0)
            {
                value++;
            }
            return value;
        }

        /// <summary>
        /// Decodes a unary-coded syntax element with different contexts per bin.
        /// </summary>
        public int DecodeUnaryWithContexts(int maxValue, ReadOnlySpan<int> contextIndices)
        {
            int value = 0;
            while (value < maxValue && value < contextIndices.Length)
            {
                if (DecodeBin(contextIndices[value]) == 0)
                    break;
                value++;
            }
            return value;
        }

        /// <summary>
        /// Decodes an absolute level for residual coding.
        /// Uses HEVC's base_level + coeff_abs_level_remaining scheme.
        /// </summary>
        /// <param name="baseLevel">Base level already decoded (significant_coeff_flag + coeff_abs_level_greater1_flag + coeff_abs_level_greater2_flag).</param>
        /// <param name="riceParameter">Rice parameter for remaining level coding.</param>
        public int DecodeAbsoluteLevel(int baseLevel, int riceParameter)
        {
            if (baseLevel < 3)
                return baseLevel;

            // Decode coeff_abs_level_remaining
            uint remaining = DecodeTruncatedRice(riceParameter, 3);
            return baseLevel + (int)remaining;
        }

        /// <summary>
        /// Renormalizes the arithmetic decoder state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Renormalize()
        {
            // ivlCurrRange >= 6 (the smallest LPS range): at most 6 doublings, all the window's look-ahead takes
            int n = System.Numerics.BitOperations.LeadingZeroCount(ivlCurrRange) - 23;
            ivlCurrRange <<= n;
            k -= n;
            if (k < 8) Refill();
        }

        // look-ahead bytes into the window (zeros past the end)
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Refill()
        {
            while (k <= 40)   // value holds 9 + k <= 57 bits
            {
                value = (value << 8) | (pos < data.Length ? data[pos] : 0u);
                pos++;
                k += 8;
            }
        }

        // (Re)starts the engine at a byte offset: ivlOffset from the next 9 bits, ivlCurrRange 510. With fewer than two
        // bytes left the engine reads no offset bits there (ivlOffset 0), as it always has.
        private void InitEngine(int byteOffset)
        {
            ivlCurrRange = 510;
            value = 0;
            pos = byteOffset;
            k = byteOffset < data.Length - 1 ? -9 : 0;
            Refill();
        }

        // the spec engine's bit position (it stops at the end of the data)
        private readonly int SpecBitPosition => Math.Min((pos * 8) - k, data.Length * 8);

        private readonly int SpecBytePosition => SpecBitPosition >> 3;

        private readonly int SpecBitsRemaining => 8 - (SpecBitPosition & 7);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private readonly void DiagLog(char mode, int ctx, byte state) =>
            DiagBinLog!.Add((DiagBinCount, mode, ctx, -1, ivlCurrRange, (uint)(value >> k), state));

        /// <summary>
        /// Gets the current bit position for debugging.
        /// </summary>
        public readonly int CurrentBitPosition => SpecBitPosition;

        /// <summary>
        /// Gets the remaining bytes in the stream.
        /// </summary>
        public readonly int RemainingBytes => data.Length - SpecBytePosition;

        // DIAG: expose internal state for debugging
        public readonly uint DiagIvlOffset => (uint)(value >> k);
        public readonly uint DiagIvlRange => ivlCurrRange;
        public readonly int DiagBytePos => SpecBytePosition;
        public readonly int DiagBitsRemaining => SpecBitsRemaining;
        public int DiagBinCount;
        public bool DiagTraceBins;
        public List<(int BinIdx, char Mode, int CtxIdx, int BinVal, uint PreRange, uint PreOffset, byte CtxState)>? DiagBinLog;

        /// <summary>
        /// Exports current context states for dependent slice decoding.
        /// </summary>
        public readonly void ExportContexts(Span<byte> destination)
        {
            int count = Math.Min(contextStates.Length, destination.Length);
            contextStates.Slice(0, count).CopyTo(destination);
        }

        /// <summary>
        /// Saves current context states to an external buffer for WPP.
        /// Called after decoding the 2nd CTU of each row.
        /// </summary>
        public readonly void SaveContextStates(Span<byte> destination)
        {
            contextStates.Slice(0, HevcCabacContextIndex.TotalContexts).CopyTo(destination);
        }

        /// <summary>
        /// Loads saved context states from a WPP buffer.
        /// Called at the start of each CTU row (except the first).
        /// </summary>
        public void LoadContextStates(ReadOnlySpan<byte> source)
        {
            source.Slice(0, HevcCabacContextIndex.TotalContexts).CopyTo(contextStates);
        }

        /// <summary>
        /// Byte-aligns and re-initializes the CABAC arithmetic engine at the current position.
        /// Matches FFmpeg's cabac_reinit (skip_bytes with 0).
        /// Used for WPP row transitions in serial decode mode.
        /// Returns false if past end of data (matches FFmpeg returning AVERROR_INVALIDDATA).
        /// </summary>
        public bool ByteAlignAndReinit()
        {
            // Determine the actual byte position accounting for buffered bits
            // FFmpeg's skip_bytes: ptr = bytestream; if (low & 0x1) ptr--;
            // Our equivalent: find the byte boundary of where we actually are
            int actualBytePos = SpecBytePosition;
            if (SpecBitsRemaining < 8)
                actualBytePos++; // we've partially consumed a byte, move past it

            // Re-initialize CABAC engine at this byte position; past the end of the data FFmpeg's skip_bytes returns NULL
            InitEngine(actualBytePos);
            return actualBytePos < data.Length - 1;
        }

        /// <summary>
        /// Re-initializes the CABAC decoder on a sub-segment of the original data.
        /// Used for WPP where each row has its own entry point in the bitstream.
        /// </summary>
        public void ReinitAtOffset(int byteOffset) => InitEngine(byteOffset);

        /// <summary>
        /// Reads raw bytes from the underlying data at an absolute offset (diagnostic only).
        /// </summary>
        public readonly byte[] DiagReadBytesAt(int offset, int count)
        {
            int available = Math.Min(count, data.Length - offset);
            if (available <= 0) return Array.Empty<byte>();
            return data.Slice(offset, available).ToArray();
        }

        /// <summary>
        /// Skips N bytes of raw data from the bitstream (used for PCM sample data).
        /// Returns a span pointing to the skipped bytes and reinitializes the CABAC engine
        /// after the skipped region. Matches FFmpeg's skip_bytes() in cabac_functions.h.
        /// </summary>
        public ReadOnlySpan<byte> SkipBytes(int numBytes)
        {
            // FFmpeg's skip_bytes: ptr = bytestream; if (low & 0x1) ptr--;
            // The "bytestream" pointer in FFmpeg points to the next byte to be read.
            // If low bit of 'low' is set, one byte was over-read — back up one.
            int rawStart = SpecBytePosition;
            if (SpecBitsRemaining < 8)
                rawStart++; // partially consumed byte — move past it

            // Extract the raw byte span
            int available = data.Length - rawStart;
            if (numBytes > available)
                numBytes = available;

            var pcmData = data.Slice(rawStart, numBytes);

            // Reinitialize CABAC engine after the skipped bytes
            InitEngine(rawStart + numBytes);
            return pcmData;
        }

        /// <summary>
        /// Gets the raw context state byte for a specific context index.
        /// State byte = (stateIdx &lt;&lt; 1) | valMps.
        /// </summary>
        public readonly byte GetContextState(int contextIndex) => contextStates[contextIndex];
    }
}
