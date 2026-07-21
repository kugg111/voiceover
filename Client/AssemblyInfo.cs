using System.Runtime.CompilerServices;

// Lets Client.Tests exercise several Windows-only internal classes
// directly (Nsnet2Processor, SileroVadProcessor, FourierTransform,
// Resampler48kTo16k, NoiseSuppressionProcessor, ...) without making their
// implementation details public. E2eeService's own equivalent attribute
// moved with it to Client.Core/AssemblyInfo.cs - see the Linux client plan.
[assembly: InternalsVisibleTo("Client.Tests")]
