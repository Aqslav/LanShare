namespace LanShare.Models;

/// <summary>Комп'ютер, знайдений у локальній мережі.</summary>
public sealed record NetworkComputer(string Address, string? HostName, bool IsThisPc)
{
    public string DisplayName => string.IsNullOrWhiteSpace(HostName) ? Address : HostName!;

    public string Subtitle => IsThisPc ? $"{Address}  ·  цей комп'ютер" : Address;
}

/// <summary>Спільна папка на віддаленому комп'ютері.</summary>
public sealed record ShareItem(string Name, string Remark, string UncPath)
{
    public string Description => string.IsNullOrWhiteSpace(Remark) ? UncPath : $"{Remark}  ·  {UncPath}";
}

/// <summary>Спільна папка цього комп'ютера.</summary>
public sealed record LocalShare(string Name, string Path);
