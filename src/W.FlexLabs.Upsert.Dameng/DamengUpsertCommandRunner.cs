using System.Data;
using System.Linq.Expressions;
using FlexLabs.EntityFrameworkCore.Upsert;
using FlexLabs.EntityFrameworkCore.Upsert.Internal.Expressions;
using FlexLabs.EntityFrameworkCore.Upsert.Runners;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace W.FlexLabs.Upsert.Dameng;

/// <summary>Executes FlexLabs upserts on the Dameng WDriver provider.</summary>
public sealed class DamengUpsertCommandRunner : RelationalUpsertCommandRunner
{
    public override bool Supports(string providerName) => providerName == "W.EntityFrameworkCore.Dameng.WDriver";

    protected override string EscapeName(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    protected override string SourcePrefix => "s.";
    protected override string TargetPrefix => "t.";
    protected override string Parameter(int index) => FormattableString.Invariant($":p{index}");

    // FlexLabs always includes the first row. One source row per MERGE avoids
    // UNION type coercion and lets repeated keys in a batch apply in input order.
    protected override int? MaxQueryParams => 1;

    public override string GenerateCommand(
        string tableName,
        ICollection<ICollection<(string ColumnName, ConstantValue Value, string? DefaultSql, bool AllowInserts)>> entities,
        ICollection<(string ColumnName, bool IsNullable)> joinColumns,
        ICollection<(string ColumnName, IKnownValue Value)>? updateExpressions,
        KnownExpression? updateCondition,
        bool returnResult = false)
    {
        if (returnResult)
            throw new NotSupportedException("Dameng MERGE does not support FlexLabs RunAndReturn.");

        var row = entities.Single();
        var source = string.Join(", ", row.Select(column =>
            (column.DefaultSql ?? ExpandValue(column.Value)) + " AS " + EscapeName(column.ColumnName)));
        var predicate = string.Join(" AND ", joinColumns.Select(column =>
        {
            var name = EscapeName(column.ColumnName);
            var equality = $"t.{name} = s.{name}";
            return column.IsNullable
                ? $"({equality} OR (t.{name} IS NULL AND s.{name} IS NULL))"
                : equality;
        }));
        var insertColumns = row.Where(column => column.AllowInserts)
            .Select(column => EscapeName(column.ColumnName)).ToArray();
        if (insertColumns.Length == 0 || joinColumns.Count == 0)
            throw new NotSupportedException("Dameng upsert requires insertable columns and a match key.");

        var sql = $"MERGE INTO {tableName} t USING (SELECT {source} FROM DUAL) s ON ({predicate})"
            + " WHEN NOT MATCHED THEN INSERT (" + string.Join(", ", insertColumns)
            + ") VALUES (" + string.Join(", ", insertColumns.Select(name => "s." + name)) + ")";
        if (updateExpressions is not null)
        {
            sql += " WHEN MATCHED THEN UPDATE SET " + string.Join(", ", updateExpressions.Select(
                expression => "t." + EscapeName(expression.ColumnName) + " = " + ExpandValue(expression.Value)));
            if (updateCondition is not null)
                sql += " WHERE " + ExpandExpression(updateCondition);
        }

        return sql;
    }

    protected override string ExpandExpression(KnownExpression expression, Func<string, string>? expandLeftColumn = null)
        => expression.ExpressionType switch
        {
            ExpressionType.And => Function("BITAND", expression, expandLeftColumn),
            ExpressionType.Or => Function("BITOR", expression, expandLeftColumn),
            ExpressionType.Modulo => Function("MOD", expression, expandLeftColumn),
            _ => base.ExpandExpression(expression, expandLeftColumn)
        };

    private string Function(string name, KnownExpression expression, Func<string, string>? expandLeftColumn)
        => name + "(" + ExpandValue(expression.Value1, expandLeftColumn)
            + ", " + ExpandValue(expression.Value2!, expandLeftColumn) + ")";

    public override int Run<TEntity>(DbContext dbContext, IEntityType entityType,
        ICollection<TEntity> entities, UpsertCommandArgs<TEntity> commandArgs)
    {
        var database = dbContext.Database;
        var current = database.CurrentTransaction;
        ValidateTransaction(database, current);

        using var owned = current is null ? database.BeginTransaction(IsolationLevel.ReadCommitted) : null;
        var savepoint = current is null ? null : NewSavepointName();
        if (savepoint is not null)
            current!.CreateSavepoint(savepoint);

        try
        {
            database.ExecuteSqlRaw(LockTable(entityType));
            var affected = base.Run(dbContext, entityType, entities, commandArgs);
            owned?.Commit();
            return affected;
        }
        catch
        {
            if (savepoint is not null)
                current!.RollbackToSavepoint(savepoint);
            throw;
        }
    }

    public override async Task<int> RunAsync<TEntity>(DbContext dbContext, IEntityType entityType,
        ICollection<TEntity> entities, UpsertCommandArgs<TEntity> commandArgs, CancellationToken cancellationToken)
    {
        var database = dbContext.Database;
        var current = database.CurrentTransaction;
        ValidateTransaction(database, current);

        await using var owned = current is null
            ? await database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false)
            : null;
        var savepoint = current is null ? null : NewSavepointName();
        if (savepoint is not null)
            await current!.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);

        try
        {
            await database.ExecuteSqlRawAsync(LockTable(entityType), cancellationToken).ConfigureAwait(false);
            var affected = await base.RunAsync(dbContext, entityType, entities, commandArgs, cancellationToken)
                .ConfigureAwait(false);
            if (owned is not null)
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch
        {
            if (savepoint is not null)
                await current!.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static string NewSavepointName() => "WUPSERT_" + Guid.NewGuid().ToString("N");

    private static void ValidateTransaction(DatabaseFacade database, IDbContextTransaction? current)
    {
        if (System.Transactions.Transaction.Current is not null || (database.GetService<IDbContextTransactionManager>() as ITransactionEnlistmentManager)
                ?.EnlistedTransaction is not null)
            throw new NotSupportedException("Dameng upsert does not support ambient or enlisted transactions.");

        if (current is not null
            && (current.GetDbTransaction().IsolationLevel != IsolationLevel.ReadCommitted || !current.SupportsSavepoints))
        {
            throw new NotSupportedException("Dameng upsert requires an EF transaction with ReadCommitted isolation and savepoints.");
        }
    }

    // Serialize MERGE against other writers without treating an arbitrary unique-key
    // exception as a match. The transaction keeps the lock through the entire batch.
    private string LockTable(IEntityType entityType)
    {
        if (entityType.GetTableName() is null)
            throw new NotSupportedException("Dameng upsert requires a physical table.");
        return "LOCK TABLE " + GetTableName(entityType) + " IN SHARE ROW EXCLUSIVE MODE";
    }
}
