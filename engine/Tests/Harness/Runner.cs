// Reflection test runner for engine/run-tests.sh: [Test]/[TestCase]/[SetUp]/[TearDown] in namespace Graze.Tests; args filter by Class.Method.
using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
public static class Runner
{
    public static int Main(string[] args)
    {
        int pass = 0, fail = 0;
        foreach (var type in typeof(Runner).Assembly.GetTypes().Where(t => t.Namespace == "Graze.Tests").OrderBy(t => t.Name))
            foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<TestAttribute>() != null || m.GetCustomAttributes<TestCaseAttribute>().Any()))
            foreach (var args0 in method.GetCustomAttributes<TestCaseAttribute>().Any() ? method.GetCustomAttributes<TestCaseAttribute>().Select(c => c.Args) : new[] { (object[])null })
            {
                if (args.Length > 0 && !args.Any(a => (type.Name + "." + method.Name).Contains(a))) continue;
                var instance = Activator.CreateInstance(type);
                try
                {
                    foreach (var s in type.GetMethods().Where(m => m.GetCustomAttribute<SetUpAttribute>() != null)) s.Invoke(instance, null);
                    method.Invoke(instance, args0 == null ? null : args0.Select((a, i) => a == null ? null : Convert.ChangeType(a, method.GetParameters()[i].ParameterType.IsEnum ? Enum.GetUnderlyingType(method.GetParameters()[i].ParameterType) : method.GetParameters()[i].ParameterType)).Select((a, i) => method.GetParameters()[i].ParameterType.IsEnum ? Enum.ToObject(method.GetParameters()[i].ParameterType, a) : a).ToArray());
                    foreach (var s in type.GetMethods().Where(m => m.GetCustomAttribute<TearDownAttribute>() != null)) s.Invoke(instance, null);
                    pass++;
                }
                catch (TargetInvocationException e) { fail++; Console.WriteLine($"FAIL {type.Name}.{method.Name}({(args0 == null ? "" : string.Join(",", args0))}): {e.InnerException.GetType().Name}: {e.InnerException.Message}"); }
            }
        Console.WriteLine($"passed {pass}, failed {fail}");
        return fail == 0 ? 0 : 1;
    }
}
