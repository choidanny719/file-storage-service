using Npgsql;

namespace FileStorage.Api;

public sealed class MetadataStore(NpgsqlDataSource source)
{
    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var gate = Command(connection, transaction, "SELECT pg_advisory_xact_lock(801987120)"))
            await gate.ExecuteNonQueryAsync(ct);
        await using var resource = typeof(MetadataStore).Assembly.GetManifestResourceStream("FileStorage.Api.schema.sql")!;
        using var reader = new StreamReader(resource);
        await using var command = Command(connection, transaction, await reader.ReadToEndAsync(ct));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<T> WithOwnerAsync<T>(string owner, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> action, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using (var gate = Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", owner))
            await gate.ExecuteNonQueryAsync(ct);
        var result = await action(connection, transaction);
        await transaction.CommitAsync(ct);
        return result;
    }

    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct) => source.OpenConnectionAsync(ct);

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, params object[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 120 };
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        return command;
    }
}
