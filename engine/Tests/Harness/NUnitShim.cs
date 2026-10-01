// Minimal NUnit surface used by engine/Tests, for engine/run-tests.sh. Extend when a test needs more.
using System;
using System.Collections;
using System.Linq;
namespace NUnit.Framework
{
    [AttributeUsage(AttributeTargets.Method)] public sealed class TestAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)] public sealed class TestCaseAttribute : Attribute { public readonly object[] Args; public TestCaseAttribute(params object[] args) { Args = args; } }
    [AttributeUsage(AttributeTargets.Method)] public sealed class SetUpAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Method)] public sealed class TearDownAttribute : Attribute { }
    public sealed class AssertionException : Exception { public AssertionException(string m) : base(m) { } }
    public sealed class Constraint
    {
        public Func<object, bool> Check; public string Text; internal object Expected;
        public Constraint Within(double tol) { var e = Convert.ToDouble(Expected); return new Constraint { Check = v => Math.Abs(Convert.ToDouble(v) - e) <= tol, Text = $"{e} ±{tol}" }; }
    }
    public static class Is
    {
        public static Constraint InRange(double a, double b) => new Constraint { Check = v => Convert.ToDouble(v) >= a && Convert.ToDouble(v) <= b, Text = $"in [{a},{b}]" };
        public static Constraint EqualTo(object e) => new Constraint { Check = v => Equals(v, e) || (IsNum(v) && IsNum(e) && Convert.ToDouble(v) == Convert.ToDouble(e)), Text = "equal to " + e, Expected = e };
        internal static bool IsNum(object o) => o is IConvertible && !(o is string) && !(o is bool);
    }
    public static class Assert
    {
        static void Fail(string what, string msg) => throw new AssertionException(what + (msg == null ? "" : " — " + msg));
        public static void That(object v, Constraint c, string m = null) { if (!c.Check(v)) Fail($"Expected {c.Text} but was {v}", m); }
        public static void IsTrue(bool c, string m = null) { if (!c) Fail("Expected true", m); }
        public static void IsFalse(bool c, string m = null) { if (c) Fail("Expected false", m); }
        public static void IsNull(object o, string m = null) { if (o != null) Fail("Expected null", m); }
        public static void IsNotNull(object o, string m = null) { if (o == null) Fail("Expected not null", m); }
        public static void AreSame(object a, object b, string m = null) { if (!ReferenceEquals(a, b)) Fail("Expected same", m); }
        public static void AreEqual(double e, double a, double tol, string m = null) { if (!(Math.Abs(e - a) <= tol) && !(e == a)) Fail($"Expected {e:R} ±{tol} but was {a:R}", m); }
        public static void AreEqual(object e, object a, string m = null)
        {
            bool ok = Equals(e, a) || (Is.IsNum(e) && Is.IsNum(a) && Convert.ToDouble(e) == Convert.ToDouble(a));
            if (!ok) Fail($"Expected {e} but was {a}", m);
        }
        public static void AreNotEqual(object e, object a, string m = null) { if (Equals(e, a)) Fail($"Expected not {e}", m); }
        public static void Greater(double a, double b, string m = null) { if (!(a > b)) Fail($"Expected {a} > {b}", m); }
        public static void GreaterOrEqual(double a, double b, string m = null) { if (!(a >= b)) Fail($"Expected {a} >= {b}", m); }
        public static void Less(double a, double b, string m = null) { if (!(a < b)) Fail($"Expected {a} < {b}", m); }
        public static void LessOrEqual(double a, double b, string m = null) { if (!(a <= b)) Fail($"Expected {a} <= {b}", m); }
        public static T Throws<T>(Action a, string m = null) where T : Exception
        {
            try { a(); } catch (T e) when (e.GetType() == typeof(T)) { return e; } catch (Exception e) { Fail($"Expected {typeof(T).Name} but got {e.GetType().Name}: {e.Message}", m); }
            Fail($"Expected {typeof(T).Name}", m); return null;
        }
        public static void DoesNotThrow(Action a, string m = null) { try { a(); } catch (Exception e) { Fail("Unexpected " + e.GetType().Name + ": " + e.Message, m); } }
    }
    public static class CollectionAssert
    {
        public static void AreEqual(IEnumerable e, IEnumerable a, string m = null)
        {
            var x = e.Cast<object>().ToArray(); var y = a.Cast<object>().ToArray();
            if (x.Length != y.Length) throw new AssertionException($"Length {x.Length} vs {y.Length} {m}");
            for (int i = 0; i < x.Length; i++) if (!Equals(x[i], y[i])) throw new AssertionException($"Differs at {i}: {x[i]} vs {y[i]} {m}");
        }
    }
}

public static class ArrayShim { public static void Fill<T>(T[] a, T v) { for (int i = 0; i < a.Length; i++) a[i] = v; } }
