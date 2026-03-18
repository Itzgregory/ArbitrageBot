namespace ArbitrageBot.Domain.Interfaces;

public interface IStale
{
    int BlockNumber { get; }
    bool IsStale(int currentBlockNumber, int maxBlockAge = 2);
}