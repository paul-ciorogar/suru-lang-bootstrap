namespace Suru.Tests;

// Verifies the IR backend's string method suite and Int64↔String conversions.
//
// String representation — every Suru String is a `ptr` to a heap-allocated
// %suru.Seq = { i64 len, ptr data }. `data` points to a null-terminated i8 buffer.
// `len` holds the character count (excluding the null terminator). String literals are
// interned as [N x i8] private constant globals; EmitStringLiteralValue wraps each
// occurrence in a fresh 16-byte Seq header via malloc (one header per runtime site, one
// global per unique source string).
//
// String methods — all live in IRStringCodeGenerator.cs (partial class IRCodeGenerator):
//
//   .len()          → Int64  — loads the len field directly from the Seq (no C call needed)
//   .equals(other)  → Bool   — extracts both data ptrs; strcmp → icmp eq i32 result, 0
//   .append(other)  → String — extract both lens+data; totalLen = len1+len2; malloc
//                              (totalLen+1); memcpy lhs; GEP to midpoint; memcpy rhs;
//                              store null terminator; EmitCreateStringSeq
//   .slice(from, to)→ String — GEP to from in source data; memcpy (to-from) bytes;
//                              null-terminate; EmitCreateStringSeq
//   .at(idx)        → String — GEP to idx; malloc 2; copy char; null-terminate;
//                              EmitCreateStringSeq with len=1
//   .ord()          → Int64  — extract data ptr; load i8; zext to i64
//
// Int64 conversions:
//
//   Int64.from(str) — extract data ptr from the argument Seq;
//                     call i64 @strtol(ptr data, ptr null, i32 10)
//   n.toString()    — two-call snprintf pattern: snprintf(null, 0, "%lld", val) measures
//                     the length (C99-defined return value, no buffer needed); malloc
//                     (count+1) bytes; snprintf(buf, count+1, ...) writes the digits;
//                     EmitCreateStringSeq wraps the result.
[Collection("IntegrationIR")]
public class IRStringTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("strings");
    private bool _testPassed;

    [Fact]
    public void Strings_PrintsExpectedOutput()
    {
        Assert.Equal("5\ntrue\nfalse\nhello world\nel\n42\n42\nh\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("strings"); }
}
