// AgentState/ReportCalibration 是静态状态——全程序集串行，防类间互踩（与主测试工程同款纪律）。
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
