namespace RatioGhost.Desktop.Platform;

internal interface IWindowsRunKey
{
    string? Read();

    void Write(string command);

    void Delete();
}
