// Covered fixtures: arrays
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Types;

namespace Suru.Compiler.Codegen;

// Array IR emission for IRCodeGenerator.
//
// ── Representation ───────────────────────────────────────────────────────────
//
// Every Suru Array value is a `ptr` to a heap-allocated
//   %suru.Array = { i64 len, i64 cap, ptr data }   (24 bytes)
//
// This is distinct from %suru.Seq (strings, 16 bytes) — arrays carry an
// explicit capacity field so that `.add(v)` can grow amortised without
// realloc on every call.  Field indices:
//   0 → i64 len   current number of elements
//   1 → i64 cap   allocated capacity (in elements, not bytes)
//   2 → ptr data  flat i64[] data buffer (8 bytes per element)
//
// Elements of any scalar type are stored as 64-bit integers regardless
// of their source type.  Pointer types (String, Array, Struct) are stored
// via `ptrtoint ptr to i64` and restored via `inttoptr i64 to ptr`.
//   Bool    → zext i1  to i64   / trunc i64 to i1
//   Int64   → identity (no instruction)
//   Float64 → bitcast double to i64 / bitcast i64 to double
//   Ptr     → ptrtoint ptr to i64 / inttoptr i64 to ptr
//
// ── Growth strategy (EmitArrayAdd) ──────────────────────────────────────────
//
// One branch on `len == cap`; inside the grow path, two `select` instructions
// implement the threshold policy with no additional branches:
//   cap == 0         → new_cap = 4         (first push)
//   0 < cap < 1024   → new_cap = cap * 2   (doubling)
//   cap >= 1024      → new_cap = cap + 1024 (linear)
//
// After the grow-or-skip branch, the element is stored at [old_len] and
// len is incremented.  The data ptr is always reloaded from the header after
// the branch merge because realloc may have moved it.
//
// ── Clone / Drop ─────────────────────────────────────────────────────────────
//
// clone(arr):
//   Scalar element types (Int64, Float64, Bool): bitwise memcpy of the buffer.
//   Pointer element types (String, Struct, Array): loop over elements, cloning
//   each one before storing the new pointer in the new buffer.
//   Returns a new %suru.Array header with cap = len (exact fit).
//
//   Nested Array elements get a shallow clone (header + buffer copy) because
//   the element type of the nested array is not available at this level — the
//   _arrayElementTypes map is keyed by variable name, not by SSA value.
//
// drop(arr):
//   Scalar element types: free(data), free(header).
//   Pointer element types: loop, drop each element, then free(data), free(header).
//   Nested Array elements are shallow-dropped (free data + header only).
//
// ── argv vs regular arrays ────────────────────────────────────────────────────
//
// The `args` parameter of suru_main holds a %suru.Seq (not %suru.Array) where
// data = argv (char**).  It is never registered in _arrayElementTypes, so
// Array dispatch in EmitMethodCall falls through to EmitArgAt.
partial class IRCodeGenerator
{
    // ─── Low-level %suru.Array GEP helpers ──────────────────────────────────

    // Load the `len` field (field index 0) from a %suru.Array header.
    private string EmitExtractArrayLen(string arrVal)
    {
        var gep = NextTmp();
        var len = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 0");
        _funcs.AppendLine($"  {len} = load i64, ptr {gep}");
        return len;
    }

    // Load the `cap` field (field index 1) from a %suru.Array header.
    private string EmitExtractArrayCap(string arrVal)
    {
        var gep = NextTmp();
        var cap = NextTmp();
        _funcs.AppendLine($"  {gep} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 1");
        _funcs.AppendLine($"  {cap} = load i64, ptr {gep}");
        return cap;
    }

    // Load the `data` pointer (field index 2) from a %suru.Array header.
    private string EmitExtractArrayData(string arrVal)
    {
        var gep  = NextTmp();
        var data = NextTmp();
        _funcs.AppendLine($"  {gep}  = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 2");
        _funcs.AppendLine($"  {data} = load ptr, ptr {gep}");
        return data;
    }

    // ─── Array literal ───────────────────────────────────────────────────────

    // Emit a `[e1, e2, ...]` array literal.
    //
    // Allocates a 24-byte %suru.Array header.  For empty literals the data ptr
    // is stored as null and cap=0; for non-empty literals: malloc count*8 bytes,
    // emit each element via EmitToI64, store at GEP-indexed slots, then build
    // the header with len=cap=count.
    //
    // Sets _pendingArrayElemType so the enclosing LetStatement can record the
    // element type in _arrayElementTypes.
    private (string val, SuruType type) EmitArrayLiteral(ArrayLiteralExpression lit)
    {
        var hdrPtr = NextTmp();
        _funcs.AppendLine($"  {hdrPtr} = call ptr @malloc(i64 24)");

        string dataPtr;
        SuruType elemType = SuruType.Int64;
        var count = lit.Elements.Count;

        if (count == 0)
        {
            dataPtr = "null";
        }
        else
        {
            var (firstVal, firstType) = EmitValue(lit.Elements[0]);
            elemType = firstType;

            var byteCount = (count * 8).ToString();
            var rawData = NextTmp();
            _funcs.AppendLine($"  {rawData} = call ptr @malloc(i64 {byteCount})");
            dataPtr = rawData;

            var slot0 = NextTmp();
            _funcs.AppendLine($"  {slot0} = getelementptr i64, ptr {dataPtr}, i64 0");
            var as64_0 = EmitToI64(firstVal, firstType);
            _funcs.AppendLine($"  store i64 {as64_0}, ptr {slot0}");

            for (int i = 1; i < count; i++)
            {
                var (elemVal, elemValType) = EmitValue(lit.Elements[i]);
                var slot = NextTmp();
                _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {dataPtr}, i64 {i}");
                var as64 = EmitToI64(elemVal, elemValType);
                _funcs.AppendLine($"  store i64 {as64}, ptr {slot}");
            }
        }

        // Build %suru.Array header: { len, cap, data }.
        var lenGep  = NextTmp();
        var capGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {count}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {count}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {hdrPtr}, i32 0, i32 2");
        _funcs.AppendLine($"  store ptr {dataPtr}, ptr {dataGep}");

        _pendingArrayElemType = elemType;
        return (hdrPtr, SuruType.Array);
    }

    // ─── Array instance methods ──────────────────────────────────────────────

    // .len() → Int64: load the len field from the %suru.Array header.
    private (string val, SuruType type) EmitArrayLen(string arrVal)
        => (EmitExtractArrayLen(arrVal), SuruType.Int64);

    // .at(i) → elemType: GEP into the i64 data buffer, load raw i64, apply FromI64.
    //
    // Elements are stored in their 64-bit encoding regardless of type.
    // EmitFromI64 restores the original type from raw bits.
    private (string val, SuruType type) EmitArrayAt(
        string arrVal, SuruType elemType, Expression idxExpr)
    {
        var data    = EmitExtractArrayData(arrVal);
        var (idx, _) = EmitValue(idxExpr);
        var slot    = NextTmp();
        var raw     = NextTmp();
        _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {data}, i64 {idx}");
        _funcs.AppendLine($"  {raw}  = load i64, ptr {slot}");
        var result = EmitFromI64(raw, elemType);
        return (result, elemType);
    }

    // .set(val, i) — store a value at index i in the data buffer.
    // Returns (0, Bool) as a side-effect-only method — the result is discarded
    // when used as a statement.
    private (string val, SuruType type) EmitArraySet(
        string arrVal, SuruType elemType, Expression valExpr, Expression idxExpr)
    {
        var data     = EmitExtractArrayData(arrVal);
        var (val, _) = EmitValue(valExpr);
        var (idx, _) = EmitValue(idxExpr);
        var slot     = NextTmp();
        _funcs.AppendLine($"  {slot} = getelementptr i64, ptr {data}, i64 {idx}");
        var as64 = EmitToI64(val, elemType);
        _funcs.AppendLine($"  store i64 {as64}, ptr {slot}");
        return ("0", SuruType.Bool);
    }

    // .add(v) — append an element, growing the data buffer when len == cap.
    //
    // Growth strategy (all via `select`, one branch total):
    //   cap == 0        → new_cap = 4
    //   0 < cap < 1024  → new_cap = cap * 2  (doubling)
    //   cap >= 1024     → new_cap = cap + 1024 (linear)
    //
    // After the grow-or-skip branch the element is stored at [old_len] and
    // len is incremented.  The data ptr is reloaded from the header after the
    // merge because realloc may have moved it.
    //
    // Returns (0, Bool) as a side-effect-only method.
    private (string val, SuruType type) EmitArrayAdd(
        string arrVal, SuruType elemType, Expression valExpr)
    {
        _externals.AddRealloc();
        var n = _arrayCounter++;

        var oldLen  = EmitExtractArrayLen(arrVal);
        var oldCap  = EmitExtractArrayCap(arrVal);
        var oldData = EmitExtractArrayData(arrVal);

        // Branch: grow only when len == cap.
        var needGrow = NextTmp();
        _funcs.AppendLine($"  {needGrow} = icmp eq i64 {oldLen}, {oldCap}");
        _funcs.AppendLine($"  br i1 {needGrow}, label %arr_grow_{n}, label %arr_store_{n}");

        // ── grow block ────────────────────────────────────────────────────────
        _funcs.AppendLine($"arr_grow_{n}:");
        // Compute new capacity: double below 1024, linear above.
        var doubled  = NextTmp();
        var linear   = NextTmp();
        var useDbl   = NextTmp();
        var grown    = NextTmp();
        var isZero   = NextTmp();
        var newCap   = NextTmp();
        var newBytes = NextTmp();
        var newData  = NextTmp();
        _funcs.AppendLine($"  {doubled}  = mul i64 {oldCap}, 2");
        _funcs.AppendLine($"  {linear}   = add i64 {oldCap}, 1024");
        _funcs.AppendLine($"  {useDbl}   = icmp ult i64 {oldCap}, 1024");
        _funcs.AppendLine($"  {grown}    = select i1 {useDbl}, i64 {doubled}, i64 {linear}");
        _funcs.AppendLine($"  {isZero}   = icmp eq i64 {oldCap}, 0");
        _funcs.AppendLine($"  {newCap}   = select i1 {isZero}, i64 4, i64 {grown}");
        _funcs.AppendLine($"  {newBytes} = mul i64 {newCap}, 8");
        _funcs.AppendLine($"  {newData}  = call ptr @realloc(ptr {oldData}, i64 {newBytes})");
        // Update cap and data fields in the header.
        var capGepG  = NextTmp();
        var dataGepG = NextTmp();
        _funcs.AppendLine($"  {capGepG}  = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {newCap}, ptr {capGepG}");
        _funcs.AppendLine($"  {dataGepG} = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 2");
        _funcs.AppendLine($"  store ptr {newData}, ptr {dataGepG}");
        _funcs.AppendLine($"  br label %arr_store_{n}");

        // ── store block (merge point) ─────────────────────────────────────────
        // Reload data ptr — it may have changed if the grow path ran.
        _funcs.AppendLine($"arr_store_{n}:");
        var curData = EmitExtractArrayData(arrVal);
        var (val, _) = EmitValue(valExpr);
        var lastSlot = NextTmp();
        _funcs.AppendLine($"  {lastSlot} = getelementptr i64, ptr {curData}, i64 {oldLen}");
        var as64 = EmitToI64(val, elemType);
        _funcs.AppendLine($"  store i64 {as64}, ptr {lastSlot}");
        var newLen  = NextTmp();
        var lenGep  = NextTmp();
        _funcs.AppendLine($"  {newLen}  = add i64 {oldLen}, 1");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {arrVal}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {newLen}, ptr {lenGep}");

        return ("0", SuruType.Bool);
    }

    // .slice(from, to) → Array: copy elements [from, to) into a new %suru.Array.
    //
    // Allocates sliceLen*8 bytes; memcpy from source data[from]; builds a new
    // 24-byte header with len=cap=sliceLen.
    // Sets _pendingArrayElemType so the LetStatement handler propagates the element type.
    private (string val, SuruType type) EmitArraySlice(
        string arrVal, Expression receiverExpr, SuruType elemType,
        Expression fromExpr, Expression toExpr)
    {
        var data      = EmitExtractArrayData(arrVal);
        var (from, _) = EmitValue(fromExpr);
        var (to, _)   = EmitValue(toExpr);

        var sliceLen  = NextTmp();
        var byteCount = NextTmp();
        var srcPtr    = NextTmp();
        var newData   = NextTmp();
        _funcs.AppendLine($"  {sliceLen}  = sub i64 {to}, {from}");
        _funcs.AppendLine($"  {byteCount} = mul i64 {sliceLen}, 8");
        _funcs.AppendLine($"  {srcPtr}    = getelementptr i64, ptr {data}, i64 {from}");
        _funcs.AppendLine($"  {newData}   = call ptr @malloc(i64 {byteCount})");
        _externals.AddMemcpy();
        _funcs.AppendLine($"  call ptr @memcpy(ptr {newData}, ptr {srcPtr}, i64 {byteCount})");

        var newHdr  = NextTmp();
        var lenGep  = NextTmp();
        var capGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {newHdr}  = call ptr @malloc(i64 24)");
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {sliceLen}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {sliceLen}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 2");
        _funcs.AppendLine($"  store ptr {newData}, ptr {dataGep}");

        _pendingArrayElemType = elemType;
        return (newHdr, SuruType.Array);
    }

    // ─── Clone ───────────────────────────────────────────────────────────────

    // Dispatch helper: extract element type from the call argument (variable lookup),
    // then delegate to EmitCloneArray.  Falls back to Int64 if the element type is
    // not known (e.g. result of a function call rather than a named variable).
    private (string val, SuruType type) EmitCloneArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        var elemType = arg is VariableReferenceExpression { Name: var n }
                       && _arrayElementTypes.TryGetValue(n, out var et) ? et : SuruType.Int64;
        return EmitCloneArray(arrVal, elemType);
    }

    // clone(arr) → Array: produce an independent copy of the array.
    //
    // Scalar element types (Int64, Float64, Bool): single memcpy of the buffer.
    // Pointer element types (String, Struct, Array): loop, clone each element.
    //   - String  → EmitCloneStringValue  (inline: new Seq + new char buffer)
    //   - Struct  → EmitCloneStruct
    //   - Array   → EmitCloneArrayShallow (header + buffer copy; nested elem type unknown)
    // The new header is allocated with cap = len (exact fit).
    private (string val, SuruType type) EmitCloneArray(string arrVal, SuruType elemType)
    {
        _externals.AddMalloc();
        var srcLen  = EmitExtractArrayLen(arrVal);
        var srcData = EmitExtractArrayData(arrVal);

        var byteCount = NextTmp();
        var newHdr    = NextTmp();
        var newData   = NextTmp();
        _funcs.AppendLine($"  {byteCount} = mul i64 {srcLen}, 8");
        _funcs.AppendLine($"  {newHdr}    = call ptr @malloc(i64 24)");
        _funcs.AppendLine($"  {newData}   = call ptr @malloc(i64 {byteCount})");

        bool isPointerElem = elemType is SuruType.String or SuruType.Struct or SuruType.Array;

        if (!isPointerElem)
        {
            // Scalar: bitwise copy — no per-element work needed.
            _externals.AddMemcpy();
            _funcs.AppendLine($"  call ptr @memcpy(ptr {newData}, ptr {srcData}, i64 {byteCount})");
        }
        else
        {
            // Pointer elements: loop, clone each one.
            var n = _arrayCounter++;

            var iSlot = NextTmp();
            _funcs.AppendLine($"  {iSlot} = alloca i64");
            _funcs.AppendLine($"  store i64 0, ptr {iSlot}");
            _funcs.AppendLine($"  br label %arr_clone_cond_{n}");

            _funcs.AppendLine($"arr_clone_cond_{n}:");
            var iVal    = NextTmp();
            var clDone  = NextTmp();
            _funcs.AppendLine($"  {iVal}   = load i64, ptr {iSlot}");
            _funcs.AppendLine($"  {clDone} = icmp eq i64 {iVal}, {srcLen}");
            _funcs.AppendLine($"  br i1 {clDone}, label %arr_clone_done_{n}, label %arr_clone_body_{n}");

            _funcs.AppendLine($"arr_clone_body_{n}:");
            var rawSlot  = NextTmp();
            var rawI64   = NextTmp();
            var elemPtr  = NextTmp();
            _funcs.AppendLine($"  {rawSlot} = getelementptr i64, ptr {srcData}, i64 {iVal}");
            _funcs.AppendLine($"  {rawI64}  = load i64, ptr {rawSlot}");
            _funcs.AppendLine($"  {elemPtr} = inttoptr i64 {rawI64} to ptr");

            // Clone the element based on its type.
            string clonedPtr = elemType switch
            {
                SuruType.String => EmitCloneStringValue(elemPtr),
                SuruType.Struct => EmitCloneStruct(elemPtr).val,
                _               => EmitCloneArrayShallow(elemPtr),  // Array: shallow
            };

            var clonedI64 = NextTmp();
            var dstSlot   = NextTmp();
            var nextI     = NextTmp();
            _funcs.AppendLine($"  {clonedI64} = ptrtoint ptr {clonedPtr} to i64");
            _funcs.AppendLine($"  {dstSlot}   = getelementptr i64, ptr {newData}, i64 {iVal}");
            _funcs.AppendLine($"  store i64 {clonedI64}, ptr {dstSlot}");
            _funcs.AppendLine($"  {nextI}     = add i64 {iVal}, 1");
            _funcs.AppendLine($"  store i64 {nextI}, ptr {iSlot}");
            _funcs.AppendLine($"  br label %arr_clone_cond_{n}");

            _funcs.AppendLine($"arr_clone_done_{n}:");
        }

        // Build new %suru.Array header with len = cap = srcLen.
        var lenGep  = NextTmp();
        var capGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {srcLen}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {srcLen}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 2");
        _funcs.AppendLine($"  store ptr {newData}, ptr {dataGep}");

        return (newHdr, SuruType.Array);
    }

    // Inline clone of a single %suru.Seq (String) value.
    //
    // Allocates a new 16-byte Seq header and a new char buffer, copies len+1
    // bytes (including the null terminator), and returns the new Seq ptr.
    // This is an internal helper, not a user-visible clone(str) built-in.
    private string EmitCloneStringValue(string seqPtr)
    {
        _externals.AddMalloc();
        _externals.AddMemcpy();

        var lenGep   = NextTmp();
        var srcLen   = NextTmp();
        var dataGep  = NextTmp();
        var srcData  = NextTmp();
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Seq, ptr {seqPtr}, i32 0, i32 0");
        _funcs.AppendLine($"  {srcLen}  = load i64, ptr {lenGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Seq, ptr {seqPtr}, i32 0, i32 1");
        _funcs.AppendLine($"  {srcData} = load ptr, ptr {dataGep}");

        // Allocate new char buffer: len+1 bytes (includes null terminator).
        var bufBytes = NextTmp();
        var newBuf   = NextTmp();
        _funcs.AppendLine($"  {bufBytes} = add i64 {srcLen}, 1");
        _funcs.AppendLine($"  {newBuf}   = call ptr @malloc(i64 {bufBytes})");
        _funcs.AppendLine($"  call ptr @memcpy(ptr {newBuf}, ptr {srcData}, i64 {bufBytes})");

        // Build new Seq header.
        var newSeq   = NextTmp();
        var newLenG  = NextTmp();
        var newDataG = NextTmp();
        _funcs.AppendLine($"  {newSeq}   = call ptr @malloc(i64 16)");
        _funcs.AppendLine($"  {newLenG}  = getelementptr %suru.Seq, ptr {newSeq}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {srcLen}, ptr {newLenG}");
        _funcs.AppendLine($"  {newDataG} = getelementptr %suru.Seq, ptr {newSeq}, i32 0, i32 1");
        _funcs.AppendLine($"  store ptr {newBuf}, ptr {newDataG}");

        return newSeq;
    }

    // Shallow clone of a nested Array element: copies the header and buffer
    // bitwise without recursing into elements.  Used when the nested element
    // type is not known (Array<Array<T>> — T is not tracked by SSA value).
    private string EmitCloneArrayShallow(string arrPtr)
    {
        _externals.AddMalloc();
        _externals.AddMemcpy();

        var srcLen  = EmitExtractArrayLen(arrPtr);
        var srcData = EmitExtractArrayData(arrPtr);

        var byteCount = NextTmp();
        var newHdr    = NextTmp();
        var newData   = NextTmp();
        _funcs.AppendLine($"  {byteCount} = mul i64 {srcLen}, 8");
        _funcs.AppendLine($"  {newHdr}    = call ptr @malloc(i64 24)");
        _funcs.AppendLine($"  {newData}   = call ptr @malloc(i64 {byteCount})");
        _funcs.AppendLine($"  call ptr @memcpy(ptr {newData}, ptr {srcData}, i64 {byteCount})");

        var lenGep  = NextTmp();
        var capGep  = NextTmp();
        var dataGep = NextTmp();
        _funcs.AppendLine($"  {lenGep}  = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 0");
        _funcs.AppendLine($"  store i64 {srcLen}, ptr {lenGep}");
        _funcs.AppendLine($"  {capGep}  = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 1");
        _funcs.AppendLine($"  store i64 {srcLen}, ptr {capGep}");
        _funcs.AppendLine($"  {dataGep} = getelementptr %suru.Array, ptr {newHdr}, i32 0, i32 2");
        _funcs.AppendLine($"  store ptr {newData}, ptr {dataGep}");

        return newHdr;
    }

    // ─── Drop ────────────────────────────────────────────────────────────────

    // Dispatch helper: extract element type from the call argument, then delegate
    // to EmitDropArray.  Falls back to Int64 (scalar path) if unknown.
    private (string val, SuruType type) EmitDropArrayDispatch(Expression arg)
    {
        var (arrVal, _) = EmitValue(arg);
        var elemType = arg is VariableReferenceExpression { Name: var n }
                       && _arrayElementTypes.TryGetValue(n, out var et) ? et : SuruType.Int64;
        return EmitDropArray(arrVal, elemType);
    }

    // drop(arr) — free the array's memory.
    //
    // Scalar element types: free(data), free(header) — two frees, no loop.
    // Pointer element types: loop, drop each element, then free(data), free(header).
    //   - String → free(data ptr), free(Seq header)
    //   - Struct → EmitDropStruct
    //   - Array  → EmitDropArrayShallow (free data + header; no recursive elem drop)
    //
    // Returns ("0", Bool) so the call can appear in expression position.
    private (string val, SuruType type) EmitDropArray(string arrVal, SuruType elemType)
    {
        _externals.AddFree();

        var srcLen  = EmitExtractArrayLen(arrVal);
        var srcData = EmitExtractArrayData(arrVal);

        bool isPointerElem = elemType is SuruType.String or SuruType.Struct or SuruType.Array;

        if (isPointerElem)
        {
            // Loop over elements, dropping each pointer before freeing the buffer.
            var n = _arrayCounter++;

            var iSlot = NextTmp();
            _funcs.AppendLine($"  {iSlot} = alloca i64");
            _funcs.AppendLine($"  store i64 0, ptr {iSlot}");
            _funcs.AppendLine($"  br label %arr_drop_cond_{n}");

            _funcs.AppendLine($"arr_drop_cond_{n}:");
            var iVal   = NextTmp();
            var drDone = NextTmp();
            _funcs.AppendLine($"  {iVal}   = load i64, ptr {iSlot}");
            _funcs.AppendLine($"  {drDone} = icmp eq i64 {iVal}, {srcLen}");
            _funcs.AppendLine($"  br i1 {drDone}, label %arr_drop_done_{n}, label %arr_drop_body_{n}");

            _funcs.AppendLine($"arr_drop_body_{n}:");
            var rawSlot = NextTmp();
            var rawI64  = NextTmp();
            var elemPtr = NextTmp();
            _funcs.AppendLine($"  {rawSlot} = getelementptr i64, ptr {srcData}, i64 {iVal}");
            _funcs.AppendLine($"  {rawI64}  = load i64, ptr {rawSlot}");
            _funcs.AppendLine($"  {elemPtr} = inttoptr i64 {rawI64} to ptr");

            switch (elemType)
            {
                case SuruType.String:
                    // Free the char buffer then the Seq header.
                    var strDataGep = NextTmp();
                    var strDataPtr = NextTmp();
                    _funcs.AppendLine($"  {strDataGep} = getelementptr %suru.Seq, ptr {elemPtr}, i32 0, i32 1");
                    _funcs.AppendLine($"  {strDataPtr} = load ptr, ptr {strDataGep}");
                    _funcs.AppendLine($"  call void @free(ptr {strDataPtr})");
                    _funcs.AppendLine($"  call void @free(ptr {elemPtr})");
                    break;
                case SuruType.Struct:
                    EmitDropStruct(elemPtr);
                    break;
                default: // Array: shallow drop
                    EmitDropArrayShallow(elemPtr);
                    break;
            }

            var nextI = NextTmp();
            _funcs.AppendLine($"  {nextI} = add i64 {iVal}, 1");
            _funcs.AppendLine($"  store i64 {nextI}, ptr {iSlot}");
            _funcs.AppendLine($"  br label %arr_drop_cond_{n}");

            _funcs.AppendLine($"arr_drop_done_{n}:");
        }

        // Free the flat data buffer and the header.
        _funcs.AppendLine($"  call void @free(ptr {srcData})");
        _funcs.AppendLine($"  call void @free(ptr {arrVal})");

        return ("0", SuruType.Bool);
    }

    // Shallow drop of a nested Array element: frees the data buffer and header
    // without recursing into its elements.  Used for Array<Array<T>> where T
    // is not available at this level.
    private void EmitDropArrayShallow(string arrPtr)
    {
        _externals.AddFree();
        var data = EmitExtractArrayData(arrPtr);
        _funcs.AppendLine($"  call void @free(ptr {data})");
        _funcs.AppendLine($"  call void @free(ptr {arrPtr})");
    }

    // ─── Element type conversion ─────────────────────────────────────────────

    // Convert a typed Suru value to i64 for storage in the i64[] data buffer.
    // Returns the SSA name of the i64 result (may be the same name if Int64).
    //
    // Bool    → zext i1 to i64         (1 bit → 64 bits, zero-extended)
    // Int64   → identity               (already i64)
    // Float64 → bitcast double to i64  (reinterpret IEEE-754 bits, no rounding)
    // Ptr     → ptrtoint ptr to i64    (pointer address as integer; 64-bit platforms only)
    private string EmitToI64(string val, SuruType type)
    {
        if (type == SuruType.Int64) return val;

        var tmp = NextTmp();
        var instr = type switch
        {
            SuruType.Bool    => $"zext i1 {val} to i64",
            SuruType.Float64 => $"bitcast double {val} to i64",
            _                => $"ptrtoint ptr {val} to i64",   // String, Array, Struct
        };
        _funcs.AppendLine($"  {tmp} = {instr}");
        return tmp;
    }

    // Restore a typed Suru value from the raw i64 stored in the data buffer.
    // Inverse of EmitToI64.
    //
    // Bool    → trunc i64 to i1        (keep low bit)
    // Int64   → identity
    // Float64 → bitcast i64 to double  (reinterpret bits back to IEEE-754)
    // Ptr     → inttoptr i64 to ptr    (integer → pointer; same address)
    private string EmitFromI64(string raw, SuruType type)
    {
        if (type == SuruType.Int64) return raw;

        var tmp = NextTmp();
        var instr = type switch
        {
            SuruType.Bool    => $"trunc i64 {raw} to i1",
            SuruType.Float64 => $"bitcast i64 {raw} to double",
            _                => $"inttoptr i64 {raw} to ptr",   // String, Array, Struct
        };
        _funcs.AppendLine($"  {tmp} = {instr}");
        return tmp;
    }
}
