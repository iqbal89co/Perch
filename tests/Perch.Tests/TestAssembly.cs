// AppPaths.Root is process-wide, and several test classes point it at their own temp folder,
// so test classes must not run in parallel.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
