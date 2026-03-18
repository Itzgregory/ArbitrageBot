using System;

namespace ArbitrageBot.Domain.Exceptions;

public abstract class ArbitrageBotException : System.Exception
{
    public string ErrorCode { get; }

    protected ArbitrageBotException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    protected ArbitrageBotException(string errorCode, string message, System.Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}