// Exposes the codecs' internal building blocks to the test project, so their parity with the reference encoders and
// decoders is verified in CI.
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("SharpImage.Tests")]
