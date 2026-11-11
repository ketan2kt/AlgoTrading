namespace TradingSystem.Application.Execution;

public static class BankNiftyFailedSetupCooldownPolicy
{
    public const int Minutes = 20;
    public const string Strategy = "Bank index directional breakout";
    public const string FailedExit = "FailedSetupExit";
    public const string Rejection = "BankNiftyFailedSetupCooldown";
    public static bool Blocks(DateTimeOffset at, DateTimeOffset? losingFailedExitAt) =>
        losingFailedExitAt is { } closed && closed <= at && at - closed < TimeSpan.FromMinutes(Minutes);

    public static bool BlocksReplay(string symbol, DateTimeOffset at, IReadOnlyList<HistoricalReplayFill> completed) =>
        completed.Any(value => value.Symbol == symbol && value.Strategy == Strategy &&
            value.ExitReason == FailedExit && value.NetPnl < 0m && !value.IsPartialExit &&
            Blocks(at, value.ExitAtUtc));
}
