using System;

namespace ArbitrageBot.Domain.Interfaces;

public interface ITimestamped
{
    DateTime FetchedAt { get; }
}