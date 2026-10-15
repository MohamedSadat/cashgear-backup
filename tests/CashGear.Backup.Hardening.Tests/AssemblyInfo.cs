// One at a time: each test drives a whole agent, kopia and test servers against the disk, and the scale suite
// measures time and memory, which a neighbour running alongside would distort.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
