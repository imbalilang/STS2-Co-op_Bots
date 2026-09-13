using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using MegaCrit.Sts2.Core.Models;

namespace CoopBots.Kernel.Vendor;

/// <summary>怪物模型数值成员的编译访问器；每个类型/成员只解析并编译一次。</summary>
internal static class MonsterValueReader
{
    private static readonly ConcurrentDictionary<(Type Type, string Name), Func<MonsterModel, int>> Accessors = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), Func<MonsterModel, bool>> BoolAccessors = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), Func<MonsterModel, object?>> ObjectAccessors = new();

    public static int ReadInt(MonsterModel monster, string name)
        => Accessors.GetOrAdd((monster.GetType(), name), static key => Build(key.Type, key.Name))(monster);

    public static bool ReadBool(MonsterModel monster, string name)
        => BoolAccessors.GetOrAdd((monster.GetType(), name), static key => BuildBool(key.Type, key.Name))(monster);

    public static object? ReadObject(MonsterModel monster, string name)
        => ObjectAccessors.GetOrAdd((monster.GetType(), name), static key => BuildObject(key.Type, key.Name))(monster);

    private static Func<MonsterModel, int> Build(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        ParameterExpression input = Expression.Parameter(typeof(MonsterModel), "monster");
        UnaryExpression typed = Expression.Convert(input, type);
        MemberExpression member = type.GetProperty(name, flags) is PropertyInfo property
            ? Expression.Property(typed, property)
            : type.GetField(name, flags) is FieldInfo field
                ? Expression.Field(typed, field)
                : throw new MissingMemberException(type.FullName, name);
        Expression value = member.Type == typeof(int) ? member : Expression.Convert(member, typeof(int));
        return Expression.Lambda<Func<MonsterModel, int>>(value, input).Compile();
    }

    private static Func<MonsterModel, bool> BuildBool(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        ParameterExpression input = Expression.Parameter(typeof(MonsterModel), "monster");
        UnaryExpression typed = Expression.Convert(input, type);
        MemberExpression member = type.GetProperty(name, flags) is PropertyInfo property
            ? Expression.Property(typed, property)
            : type.GetField(name, flags) is FieldInfo field
                ? Expression.Field(typed, field)
                : throw new MissingMemberException(type.FullName, name);
        return Expression.Lambda<Func<MonsterModel, bool>>(member, input).Compile();
    }

    private static Func<MonsterModel, object?> BuildObject(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        ParameterExpression input = Expression.Parameter(typeof(MonsterModel), "monster");
        UnaryExpression typed = Expression.Convert(input, type);
        MemberExpression member = type.GetProperty(name, flags) is PropertyInfo property
            ? Expression.Property(typed, property)
            : type.GetField(name, flags) is FieldInfo field
                ? Expression.Field(typed, field)
                : throw new MissingMemberException(type.FullName, name);
        UnaryExpression boxed = Expression.Convert(member, typeof(object));
        return Expression.Lambda<Func<MonsterModel, object?>>(boxed, input).Compile();
    }
}
