namespace Qqq.PrintAgent.Core.Printing;

public sealed record DeviceControlPoll(int ControlVersion, string ServerTime, int PollIntervalSeconds, DeviceDesiredState DesiredState, IReadOnlyList<DeviceControlCommand> Commands);
