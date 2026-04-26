namespace Suru.Tests;

// Verifies the IR backend's array implementation: literal construction, len/at/set/add/slice.
//
// ── Storage model ────────────────────────────────────────────────────────────
//
// Every Suru Array value is a `ptr` to a heap-allocated %suru.Seq = { i64 len, ptr data }.
// This is the same header layout as String. The data field points to a flat i64 buffer —
// elements of any scalar type are stored as 64-bit integers regardless of their source type:
//
//   Bool    → zext i1 to i64         (zero-extended; restored via trunc i64 to i1)
//   Int64   → identity               (already i64; no instruction emitted)
//   Float64 → bitcast double to i64  (IEEE-754 bit reinterpretation; restored via bitcast back)
//   Ptr     → ptrtoint ptr to i64    (String/Array/Struct address; restored via inttoptr)
//
// This matches the LLVMSharp CodeGenerator's ToI64/FromI64 pattern and allows the same
// flat buffer to hold any element type without per-element type tags.
//
// ── Element type tracking ────────────────────────────────────────────────────
//
// Because EmitValue only returns (SSA name, SuruType) — where SuruType for an array
// literal is always SuruType.Array — there is no channel to carry the *element* type
// through the return value. An indirection is used:
//
//   _pendingArrayElemType  — set by EmitArrayLiteral / EmitArraySlice to the element type
//   _arrayElementTypes     — Dictionary<string, SuruType>: variable name → element type
//
// The LetStatement handler in EmitStmt consumes _pendingArrayElemType after the variable
// is registered in _vars. Subsequent method dispatch (EmitMethodCall) looks up the variable
// name in _arrayElementTypes to find the element type before routing to EmitArrayAt,
// EmitArraySet, etc.
//
// ── argv vs regular arrays ────────────────────────────────────────────────────
//
// The `args` parameter of suru_main holds a %suru.Seq where data = argv (char**), not an
// i64 buffer. Because `args` is registered in _vars but never registered in _arrayElementTypes,
// method dispatch for `args.at(i)` falls through to EmitArgAt, which uses a ptr-array GEP
// (not an i64-array GEP) followed by strlen and String Seq wrapping. This disambiguates
// without any explicit flag — absence from _arrayElementTypes is the signal.
//
// ── EmitArrayAdd: realloc strategy ────────────────────────────────────────────
//
// `.add(v)` grows the data buffer by one element using realloc. The Seq header itself is a
// stable heap pointer (stored in the variable's alloca slot) — only the data pointer inside
// the header may change after realloc. After the new element is stored at [old_len], both
// Seq.data and Seq.len are updated via GEP into the existing header ptr.
//
// ── EmitArraySlice ────────────────────────────────────────────────────────────
//
// `.slice(from, to)` allocates a new i64 buffer (via malloc + memcpy), then wraps it in a
// new Seq header. It sets _pendingArrayElemType = elemType so the LetStatement handler
// propagates the element type to the new variable. The original Seq and buffer are leaked
// (no free) — same strategy as string slice.
[Collection("IntegrationIR")]
public class IRArrayTests(CompiledFixturesIR fixtures) : IntegrationTestBase
{
    private readonly string _exe = fixtures.GetExecutable("arrays");

    [Fact]
    public void Array_LenAtSetAddSlice()
    {
        // [10, 20, 30] → len=3, at(0)=10, at(2)=30
        // set(99, 1) → at(1)=99
        // add(40) → len=4, at(3)=40
        // slice(1, 3) → len=2, at(0)=99
        Assert.Equal("3\n10\n30\n99\n4\n40\n2\n99\n", Run(_exe));
    }
}
