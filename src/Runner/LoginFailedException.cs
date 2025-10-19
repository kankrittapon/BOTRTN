using System;

public sealed class LoginFailedException : Exception
{
    public LoginFailedException(string message)
        : base(message)
    {
    }
}
