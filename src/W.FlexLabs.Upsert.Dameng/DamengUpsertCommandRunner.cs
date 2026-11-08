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
    private readonly ITable? _table;

    public DamengUpsertCommandRunner() { }

    // FlexLabs removes CLR types from KnownExpression. Keep the EF table mappings
    // on a per-execution runner so the registered service keeps no mutable state.
    private DamengUpsertCommandRunner(IEntityType entityType)
        => _table = entityType.Model.GetRelationalModel().FindTable(entityType.GetTableName()!, entityType.GetSchema());

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
        var sourceColumns = row.Select(column => column.ColumnName).ToHashSet(StringComparer.Ordinal);
        if (joinColumns.Count == 0 || joinColumns.Any(column => !sourceColumns.Contains(column.ColumnName)))
            throw new NotSupportedException("Dameng upsert requires match columns mapped in the MERGE source.");
        if (updateExpressions?.Any(expression => joinColumns.Any(
            column => column.ColumnName == expression.ColumnName)) == true)
        {
            throw new NotSupportedException("Dameng MERGE cannot update a match column.");
        }

        var source = string.Join(", ", row.Select(column =>
        {
            var parameter = ExpandValue(column.Value);
            var value = column.DefaultSql is null ? parameter : $"COALESCE({parameter}, {column.DefaultSql})";
            return value + " AS " + EscapeName(column.ColumnName);
        }));
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
        if (insertColumns.Length == 0)
            throw new NotSupportedException("Dameng upsert requires insertable columns.");

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
        else if (updateCondition is not null)
        {
            // An empty update projection can still carry condition parameters.
            // Reference those bindings in a tautology without evaluating the condition.
            var bindings = updateCondition.GetConstantValues().Select(value =>
            {
                var parameter = ExpandValue(value);
                return $"({parameter} IS NULL OR {parameter} IS NOT NULL)";
            }).ToArray();
            if (bindings.Length != 0)
                sql += " WHERE " + string.Join(" AND ", bindings);
        }

        return sql;
    }

    protected override string ExpandExpression(KnownExpression expression, Func<string, string>? expandLeftColumn = null)
    {
        if (expression.ExpressionType is ExpressionType.Equal or ExpressionType.NotEqual)
        {
            var leftNull = expression.Value1 is ConstantValue { Value: null };
            var rightNull = expression.Value2 is ConstantValue { Value: null };
            if (leftNull || rightNull)
            {
                var operand = rightNull ? expression.Value1 : expression.Value2!;
                var nullValue = rightNull ? expression.Value2! : expression.Value1;
                // FlexLabs still supplies the null as a binding. WDriver rejects
                // unused bindings, so include the known-null parameter in the test.
                return $"({IsNullExpression(operand, expression.ExpressionType == ExpressionType.NotEqual)}"
                    + $" AND {ExpandValue(nullValue)} IS NULL)";
            }
        }

        return expression.ExpressionType switch
        {
            ExpressionType.Add => ExpandAddition(expression, expandLeftColumn),
            ExpressionType.And => Function("BITAND", expression, expandLeftColumn),
            ExpressionType.Or => Function("BITOR", expression, expandLeftColumn),
            ExpressionType.Modulo => Function("MOD", expression, expandLeftColumn),
            _ => base.ExpandExpression(expression, expandLeftColumn)
        };
    }

    private string ExpandAddition(KnownExpression expression, Func<string, string>? expandLeftColumn)
    {
        var left = expression.Value1;
        var right = expression.Value2!;
        return Combine(GetOperandKind(left), GetOperandKind(right)) switch
        {
            OperandKind.Text => Concatenate(left, right, "''", expandLeftColumn),
            OperandKind.LobText => Concatenate(left, right, "TO_CLOB('')", expandLeftColumn),
            OperandKind.UnsupportedText => throw new NotSupportedException(
                "Dameng upsert string addition requires two string operands without value converters."),
            _ => base.ExpandExpression(expression, expandLeftColumn)
        };
    }

    private string Concatenate(IKnownValue left, IKnownValue right, string empty,
        Func<string, string>? expandLeftColumn)
        => $"(COALESCE({ExpandValue(left, expandLeftColumn)}, {empty})"
            + $" || COALESCE({ExpandValue(right, expandLeftColumn)}, {empty}))";

#pragma warning disable EF1001 // FlexLabs 10.1.0 exposes column names only through this pinned expression API.
    private OperandKind GetOperandKind(IKnownValue value)
        => value switch
        {
            ConstantValue { Value: null } => OperandKind.Null,
            ConstantValue { Value: string, ColumnProperty: var column } =>
                GetColumnKind(column?.ColumnName, fallback: OperandKind.Text),
            PropertyValue property => GetColumnKind(property.Column.ColumnName),
            KnownExpression { ExpressionType: ExpressionType.Add or ExpressionType.Coalesce or ExpressionType.Conditional } expression
                => Combine(GetOperandKind(expression.Value1), GetOperandKind(expression.Value2!)),
            _ => OperandKind.Other
        };

#pragma warning restore EF1001

    private OperandKind GetColumnKind(string? name, OperandKind fallback = OperandKind.Other)
    {
        var mappings = name is null ? null : _table?.FindColumn(name)?.PropertyMappings;
        if (mappings is null || !mappings.Any(mapping => mapping.Property.ClrType == typeof(string)))
            return fallback;

        if (!mappings.All(mapping => mapping.Property.ClrType == typeof(string) && mapping.TypeMapping.Converter is null))
            return OperandKind.UnsupportedText;
        return mappings.Any(mapping => mapping.TypeMapping.StoreTypeNameBase is "CLOB" or "NCLOB" or "TEXT")
            ? OperandKind.LobText
            : OperandKind.Text;
    }

    private static OperandKind Combine(OperandKind left, OperandKind right)
    {
        if (left == OperandKind.Null)
            return right;
        if (right == OperandKind.Null || left == right)
            return left;
        if ((left == OperandKind.Text && right == OperandKind.LobText)
            || (left == OperandKind.LobText && right == OperandKind.Text))
            return OperandKind.LobText;
        return OperandKind.UnsupportedText;
    }

    private enum OperandKind { Other, Null, Text, LobText, UnsupportedText }

    private string Function(string name, KnownExpression expression, Func<string, string>? expandLeftColumn)
        => name + "(" + ExpandValue(expression.Value1, expandLeftColumn)
            + ", " + ExpandValue(expression.Value2!, expandLeftColumn) + ")";

    public override int Run<TEntity>(DbContext dbContext, IEntityType entityType,
        ICollection<TEntity> entities, UpsertCommandArgs<TEntity> commandArgs)
    {
        if (entities.Count == 0)
            return 0;

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
            var affected = new DamengUpsertCommandRunner(entityType)
                .ExecuteMerge(dbContext, entityType, entities, commandArgs);
            if (savepoint is not null)
                current!.ReleaseSavepoint(savepoint);
            owned?.Commit();
            return affected;
        }
        catch (Exception failure)
        {
            if (savepoint is not null)
            {
                try
                {
                    current!.RollbackToSavepoint(savepoint);
                    current.ReleaseSavepoint(savepoint);
                }
                catch (Exception recoveryFailure)
                {
                    throw new AggregateException("Dameng upsert and savepoint recovery both failed.", failure, recoveryFailure);
                }
            }
            throw;
        }
    }

    public override async Task<int> RunAsync<TEntity>(DbContext dbContext, IEntityType entityType,
        ICollection<TEntity> entities, UpsertCommandArgs<TEntity> commandArgs, CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
            return 0;

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
            var affected = await new DamengUpsertCommandRunner(entityType)
                .ExecuteMergeAsync(dbContext, entityType, entities, commandArgs, cancellationToken).ConfigureAwait(false);
            if (savepoint is not null)
                await current!.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
            if (owned is not null)
                await owned.CommitAsync(cancellationToken).ConfigureAwait(false);
            return affected;
        }
        catch (Exception failure)
        {
            if (savepoint is not null)
            {
                try
                {
                    await current!.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                    await current.ReleaseSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception recoveryFailure)
                {
                    throw new AggregateException("Dameng upsert and savepoint recovery both failed.", failure, recoveryFailure);
                }
            }
            throw;
        }
    }

    private int ExecuteMerge<TEntity>(DbContext dbContext, IEntityType entityType,
        ICollection<TEntity> entities, UpsertCommandArgs<TEntity> commandArgs) where TEntity : class
        => base.Run(dbContext, entityType, entities, commandArgs);

    private Task<int> ExecuteMergeAsync<TEntity>(DbContext dbContext, IEntityType entityType,
        ICollection<TEntity> entities, UpsertCommandArgs<TEntity> commandArgs, CancellationToken cancellationToken)
        where TEntity : class
        => base.RunAsync(dbContext, entityType, entities, commandArgs, cancellationToken);

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
        return "LOCK TABLE " + GetTableName(entityType) + " IN EXCLUSIVE MODE";
    }
}
