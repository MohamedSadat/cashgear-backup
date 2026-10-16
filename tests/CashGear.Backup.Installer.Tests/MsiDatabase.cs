using System.Runtime.InteropServices;
using System.Text;

namespace CashGear.Backup.Installer.Tests;

/// <summary>Read-only queries against an MSI's tables (msi.dll). A table the package does not have reads as empty.</summary>
internal sealed class MsiDatabase : IDisposable
{
    private const uint Success = 0;
    private const uint NoMoreItems = 259;
    private const uint MoreData = 234;
    private const uint BadQuery = 1615;
    private const uint InvalidTable = 1628;

    private nint _handle;

    public MsiDatabase(string path)
    {
        // MSIDBOPEN_READONLY is the persist "string" with value 0.
        Check(MsiOpenDatabaseW(path, 0, out _handle), "open " + path);
    }

    /// <summary>Every row, fields as strings (null for a null field).</summary>
    public List<string?[]> Query(string sql)
    {
        var rows = new List<string?[]>();
        var result = MsiDatabaseOpenViewW(_handle, sql, out var view);
        if (result is BadQuery or InvalidTable)
        {
            return rows;
        }

        Check(result, sql);
        try
        {
            Check(MsiViewExecute(view, 0), sql);
            while (true)
            {
                result = MsiViewFetch(view, out var record);
                if (result == NoMoreItems)
                {
                    return rows;
                }

                Check(result, sql);
                try
                {
                    var count = MsiRecordGetFieldCount(record);
                    var row = new string?[count];
                    for (uint i = 1; i <= count; i++)
                    {
                        row[i - 1] = MsiRecordIsNull(record, i) ? null : ReadString(record, i);
                    }

                    rows.Add(row);
                }
                finally
                {
                    MsiCloseHandle(record);
                }
            }
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    public string? Property(string name) =>
        Query($"SELECT `Value` FROM `Property` WHERE `Property` = '{name}'").SingleOrDefault()?[0];

    public void Dispose()
    {
        if (_handle != 0)
        {
            MsiCloseHandle(_handle);
            _handle = 0;
        }
    }

    private static string ReadString(nint record, uint field)
    {
        uint size = 256;
        var buffer = new StringBuilder((int)size);
        var result = MsiRecordGetStringW(record, field, buffer, ref size);
        if (result == MoreData)
        {
            size++;
            buffer = new StringBuilder((int)size);
            result = MsiRecordGetStringW(record, field, buffer, ref size);
        }

        Check(result, "read field");
        return buffer.ToString();
    }

    private static void Check(uint result, string what)
    {
        if (result != Success)
        {
            throw new InvalidOperationException($"msi.dll failed ({result}): {what}");
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabaseW(string databasePath, nint persist, out nint database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenViewW(nint database, string query, out nint view);

    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(nint view, nint record);

    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(nint view, out nint record);

    [DllImport("msi.dll")]
    private static extern uint MsiRecordGetFieldCount(nint record);

    [DllImport("msi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MsiRecordIsNull(nint record, uint field);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetStringW(nint record, uint field, StringBuilder value, ref uint size);

    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(nint handle);
}
