using Xunit;

// vendored UndertaleModLib 的 CODE chunk 解析器（引用链）不是线程安全的：
// 两个并发 UndertaleIO.Read 会互相踩状态（UndertaleSerializationException:
// "Index was outside the bounds of the array ... in chunk CODE"）。
// xUnit 默认跨 collection 并行 → 本程序集一律串行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
