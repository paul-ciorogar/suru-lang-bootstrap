namespace Suru.Tests;

// Verifies the IR backend's struct implementation: literal creation, field read/write, clone, drop.
//
// ── Storage model ────────────────────────────────────────────────────────────
//
// Every Suru Struct value is a `ptr` to the head of a heap-allocated linked list of
// %suru.Field nodes:
//
//   %suru.Field = type { ptr, i32, i64, ptr }
//                        [0]  [1]  [2]  [3]
//                        name tag  val  next
//
//   [0] name — ptr to null-terminated field name string (interned in _stringLiterals)
//   [1] tag  — i32: 0=Bool, 1=Int64, 2=Float64, 3=ptr (String/Array/Struct)
//   [2] val  — i64: field value in the same ToI64/FromI64 encoding used by arrays
//   [3] next — ptr to next node, null for the tail
//
// Node size: 32 bytes (ptr=8 + i32=4 + pad=4 + i64=8 + ptr=8).
// Field name strings are interned via _stringLiterals (same dict as String literals)
// and used as raw i8* — NOT wrapped in a Seq.
//
// ── suru_find_field ──────────────────────────────────────────────────────────
//
// An internal IR function emitted once per module (guarded by _findFieldEmitted).
// Uses a phi-loop to traverse the list and strcmp to match the field name:
//
//   %ff_node = phi ptr [ %head, entry ], [ %ff_next, continue ]
//   load name ptr from ff_node[0], strcmp against search key
//   if match → return ff_node; else load ff_node[3] → next iteration
//
// All EmitFieldAccess and EmitFieldAssignment calls go through EmitFindFieldCall.
//
// ── ResolvedType ─────────────────────────────────────────────────────────────
//
// The SemanticAnalyzer sets fa.ResolvedType on FieldAccessExpression nodes by looking
// up the variable in _structSymbols. This static type lets EmitFieldAccess call
// EmitFromI64 directly without reading the tag at runtime — the common case for
// fields declared in the same function scope.
//
// ── Clone ────────────────────────────────────────────────────────────────────
//
// EmitCloneStruct uses a while-style loop (alloca + load + icmp + branch). For each
// source node: malloc(32), copy slots 0-2, set next=null, then either set the new head
// (when cprev==null, i.e. first iteration) or wire the previous node's next slot.
// After the loop, load and return the new head ptr.
//
// ── Drop ─────────────────────────────────────────────────────────────────────
//
// EmitDropStruct loads `next` before calling @free(current) to avoid use-after-free.
// Terminates when the current node pointer is null.
//
// ── Fixture fix ──────────────────────────────────────────────────────────────
//
// The fixture had `drop(suru)` (line 23) with no matching `let suru:` — the let was
// commented out, leaving a dangling undefined-variable reference that caused a compile
// error in both backends. That line was commented out as part of this migration.
[Collection("IntegrationIR")]
public class IRStructTests(CompiledFixturesIR fixtures) : IntegrationTestBase, IDisposable
{
    private readonly string _exe = fixtures.GetExecutable("structs");
    private bool _testPassed;

    [Fact]
    public void Struct_AllPatterns()
    {
        // basic: field read/write/clone/drop
        // struct from function: return s.field via typed let
        // struct passed to function: return s.field
        // deep chain: struct.array.at(i).nested.nested.value via let intermediaries
        Assert.Equal("true\n2283\nfalse\nfalse\nSuru\n2283\n42\n99\n", Run(_exe));
        _testPassed = true;
    }

    public void Dispose() { if (!_testPassed) fixtures.RecordFailure("structs"); }
}
