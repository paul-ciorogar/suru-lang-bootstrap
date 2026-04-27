namespace Suru.Tests;

// Verifies the IR backend's array implementation: literal construction, len/at/set/add/slice,
// amortized growth, and clone/drop with deep element handling.
//
// ── Storage model ────────────────────────────────────────────────────────────
//
// Every Suru Array value is a `ptr` to a heap-allocated
//   %suru.Array = { i64 len, i64 cap, ptr data }   (24 bytes)
// distinct from %suru.Seq used by strings (16 bytes, no cap field).
//
// The data field points to a flat i64 buffer — elements of any scalar type are
// stored as 64-bit integers regardless of their source type:
//
//   Bool    → zext i1 to i64         (zero-extended; restored via trunc i64 to i1)
//   Int64   → identity               (already i64; no instruction emitted)
//   Float64 → bitcast double to i64  (IEEE-754 bit reinterpretation; restored via bitcast back)
//   Ptr     → ptrtoint ptr to i64    (String/Array/Struct address; restored via inttoptr)
//
// ── Capacity and growth ───────────────────────────────────────────────────────
//
// The cap field tracks allocated capacity (in elements).  `.add(v)` branches on
// `len == cap` and only calls realloc when growth is needed.  New capacity is
// chosen via two `select` instructions (no extra branches):
//
//   cap == 0        → new_cap = 4          (first push)
//   0 < cap < 1024  → new_cap = cap * 2   (doubling)
//   cap >= 1024     → new_cap = cap + 1024 (linear)
//
// ── Element type tracking ────────────────────────────────────────────────────
//
// Because EmitValue only returns (SSA name, SuruType) — where SuruType for an array
// literal is always SuruType.Array — there is no channel to carry the *element* type
// through the return value.  An indirection is used:
//
//   _pendingArrayElemType  — set by EmitArrayLiteral / EmitArraySlice to the element type
//   _arrayElementTypes     — Dictionary<string, SuruType>: variable name → element type
//
// The LetStatement handler in EmitStmt consumes _pendingArrayElemType after the variable
// is registered in _vars.  Subsequent method dispatch (EmitMethodCall) looks up the variable
// name in _arrayElementTypes to find the element type before routing to EmitArrayAt, etc.
//
// ── Clone / Drop ─────────────────────────────────────────────────────────────
//
// clone(arr): scalar elements → memcpy; pointer elements → loop cloning each one.
//   String  → malloc new Seq + malloc+memcpy char buffer (every Seq data ptr is heap-owned)
//   Struct  → EmitCloneStruct (linked-list traversal)
//   Array   → shallow clone (header + buffer copy; nested element type not tracked by SSA value)
//
// drop(arr): scalar elements → free(data), free(header);
//            pointer elements → loop dropping each one, then free(data), free(header).
//   String  → free(data), free(Seq header)  (safe because data is always heap-owned)
//   Struct  → EmitDropStruct
//   Array   → shallow drop (free data + header only)
//
// ── argv vs regular arrays ────────────────────────────────────────────────────
//
// The `args` parameter of suru_main holds a %suru.Seq (not %suru.Array) where
// data = argv (char**).  Because `args` is never registered in _arrayElementTypes,
// method dispatch for `args.at(i)` falls through to EmitArgAt, which uses a ptr-array
// GEP (not an i64-array GEP) followed by strlen and String Seq wrapping.
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
        // clone(nums) → len=4, at(0)=10; set(777,0) on clone → clone.at(0)=777, nums.at(0)=10
        // drop(nums3)
        // clone(words) Array<String> → len=2, at(0)="hello"
        // drop(words2), drop(words)
        // + arrays from functions, arrays passed to functions
        Assert.Equal("3\n10\n30\n99\n4\n40\n2\n99\n4\n10\n777\n10\n2\nhello\n4\n1\n4\n1 one\n5\n", Run(_exe));
    }
}
