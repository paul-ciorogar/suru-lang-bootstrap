using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Testing;

namespace Suru.Compiler.Codegen;

public sealed partial class CodeGenerator
{
    /// <summary>
    /// Emits the body of one LLVM function. It owns exactly what is per-function — the two
    /// builders and the function they build into — and reaches everything else through the
    /// <see cref="CodeGenerator"/> it belongs to: the module, the declared runtime, the interned
    /// strings and the one scope stack.
    /// <para>
    /// <b>Why a second function needs a second emitter rather than a moved builder.</b>
    /// <c>_allocas</c> sits at the end of its function's <c>entry</c> for the whole run,
    /// and that is what puts every slot in the frame however deeply branched its <c>let</c> is.
    /// A builder repositioned into another function's <c>entry</c> and back would have to be
    /// moved back <i>exactly</i>, and a slot built while it was elsewhere would silently land in
    /// the wrong frame. Two emitters cannot get that wrong: each builder is positioned once.
    /// </para>
    /// <para>
    /// <b>The scope stack is shared, not per-emitter.</b> A nested function's emitter enters a
    /// <see cref="ScopeKind.Function"/> scope on the same stack, and the barrier hides the
    /// enclosing function's variables while letting function bindings through — so nothing has to
    /// be copied in or saved and restored around the nested body.
    /// </para>
    /// <para>
    /// Nested rather than separate so it can reach the generator's private state without that
    /// state becoming anybody else's business, and <see cref="IDisposable"/> so the builders
    /// are released when a <see cref="CodegenException"/> leaves an emit half-done.
    /// </para>
    /// </summary>
    private sealed class FunctionEmitter : IDisposable
    {
        private readonly CodeGenerator _generator;

        private readonly LLVMBuilderRef _builder;

        /// <summary>
        /// Positioned at this function's entry block and never moved. Every stack slot is built
        /// through it, so a binding's slot belongs to the frame however deeply branched the
        /// statement that declares it is.
        /// </summary>
        private readonly LLVMBuilderRef _allocas;

        private readonly LLVMBasicBlockRef _body;

        public FunctionEmitter(CodeGenerator generator, LLVMValueRef function)
        {
            _generator = generator;

            var context = generator._llvmModule.Context;
            _builder = LLVMBuilderRef.Create(context);
            _allocas = LLVMBuilderRef.Create(context);

            // Two blocks rather than one: 'entry' is the frame and holds nothing but allocas,
            // 'body' holds the code. Keeping them apart is what lets _allocas sit at the end of
            // 'entry' for the whole run without later code getting in ahead of it — which one
            // block could not promise the moment a branch terminates it.
            var entry = function.AppendBasicBlock("entry");
            _body = function.AppendBasicBlock("body");

            _allocas.PositionAtEnd(entry);
            _builder.PositionAtEnd(_body);
        }

        /// <summary>
        /// Terminates <c>entry</c> with its jump into <c>body</c>. Called last, once every slot
        /// is in — a slot built after it would land behind the terminator.
        /// </summary>
        public void Finish() => _allocas.BuildBr(_body);

        public void Dispose()
        {
            _allocas.Dispose();
            _builder.Dispose();
        }

        /// <summary>Returns <paramref name="value"/> from wherever emission ended up.</summary>
        public void Return(LLVMValueRef value) => _builder.BuildRet(value);

        private void EmitStatement(Statement statement)
        {
            switch (statement)
            {
                case ExpressionStatement exprStmt:
                    EmitExpr(exprStmt.Expression);
                    break;
                case LetStatement let:
                    EmitLet(let);
                    break;
                case AssignmentStatement assignment:
                    _builder.BuildStore(EmitExpr(assignment.Value), _generator.Variable(assignment.Name).Slot);
                    break;
                case BlockStatement block:
                    // Still purely lexical: no branch and no new basic block, only a scope. An
                    // arm gets its blocks from the 'if', not from the block that is its body.
                    //
                    // This case handles every block that is not a loop body, because a loop scope's
                    // payload is the two basic blocks and those do not exist until 'EmitWhile' has
                    // made them — so that one enters its own scope. See the note there.
                    _generator._scopes.EnterNew(block.Kind);
                    EmitStatements(block.Statements);
                    _generator._scopes.Exit();
                    break;
                case IfStatement branch:
                    EmitIf(branch);
                    break;
                case WhileStatement loop:
                    EmitWhile(loop);
                    break;
                case BreakStatement:
                    _builder.BuildBr(_generator.Loop("break").Break);
                    break;
                case ContinueStatement:
                    _builder.BuildBr(_generator.Loop("continue").Continue);
                    break;
                case MockDirective mock:
                    // Emitted exactly as the assignment it is; only reaching codegen at all is
                    // what makes it a test-build feature.
                    _builder.BuildStore(EmitExpr(mock.Value), _generator.Variable(mock.Name).Slot);
                    break;
                case ViewDirective view:
                    EmitView(view);
                    break;
                case AssertDirective assert:
                    EmitAssert(assert);
                    break;
                default:
                    throw new CodegenException($"cannot emit statement '{statement.GetType().Name}'");
            }
        }

        /// <summary>
        /// Emits an expression and returns its value, or a null value reference for
        /// expressions of type <see cref="SuruType.Void"/>.
        /// </summary>
        private LLVMValueRef EmitExpr(Expression expression)
        {
            switch (expression)
            {
                case BoolLiteral b:
                    return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, b.Value ? 1ul : 0ul, false);
                case IntLiteral i:
                    return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, (ulong)i.Value, true);
                case FloatLiteral f:
                    return LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, f.Value);
                case CallExpression { Name: "printLn", Args.Count: 1 } call:
                    EmitPrintLn(call.Args[0]);
                    return default;
                case IdentifierExpression identifier:
                    var variable = _generator.Variable(identifier.Name);
                    return _builder.BuildLoad2(variable.Type, variable.Slot, identifier.Name);
                case BinaryExpression binary:
                    return EmitBinary(binary);
                case UnaryExpression unary:
                    return EmitUnary(unary);
                default:
                    throw new CodegenException($"cannot emit expression '{expression.GetType().Name}'");
            }
        }

        /// <summary>
        /// The first construct to emit more than one basic block. The condition is already an
        /// <c>i1</c> — <c>bool</c> maps to <see cref="LLVMTypeRef.Int1"/> and a comparison yields
        /// one — so it feeds the branch with nothing in between.
        /// <para>
        /// With no <c>else</c> the false edge goes straight to <c>if.end</c>: there is no
        /// alternative to run, only somewhere to be afterwards, which is why the block exists in
        /// both shapes and is named for continuing rather than for merging.
        /// </para>
        /// <para>
        /// An arm <i>can</i> leave early — a <c>break</c> or <c>continue</c> inside one terminates
        /// its block — so the branch to the end goes through <see cref="BranchTo"/> rather than
        /// being built unconditionally. When both arms leave, <c>if.end</c> has no predecessors at
        /// all, which is valid IR here only because there are no phi nodes to leave without an
        /// incoming edge: every variable is an alloca in <c>entry</c>, which dominates the whole
        /// function. That is the assumption to revisit the day an expression needs a phi, as a
        /// short-circuiting <c>and</c> would.
        /// </para>
        /// </summary>
        private void EmitIf(IfStatement branch)
        {
            // The condition is emitted first, so the function is read back from where emission
            // actually ended up rather than from where it started — which will matter the day an
            // expression can move the insert point, as a short-circuiting 'and' would.
            var condition = EmitExpr(branch.Condition);
            var function = _builder.InsertBlock.Parent;

            var otherwise = branch.Else;

            // Appended in source order so the IR reads in it. Not an LLVMBasicBlockRef? for the
            // absent arm: the type converts implicitly from a raw pointer, so a null literal
            // would quietly become a non-null nullable holding a null handle.
            var thenBlock = function.AppendBasicBlock("if.then");
            var elseBlock = otherwise is null ? default : function.AppendBasicBlock("if.else");
            var endBlock = function.AppendBasicBlock("if.end");

            _builder.BuildCondBr(condition, thenBlock, otherwise is null ? endBlock : elseBlock);

            _builder.PositionAtEnd(thenBlock);
            EmitStatement(branch.Then);
            BranchTo(endBlock);

            // 'else if' arrives here as an IfStatement and needs no case of its own: it appends
            // its own blocks, leaves the builder at its own end, and the branch below terminates
            // that block rather than this one.
            if (otherwise is not null)
            {
                _builder.PositionAtEnd(elseBlock);
                EmitStatement(otherwise);
                BranchTo(endBlock);
            }

            _builder.PositionAtEnd(endBlock);
        }

        /// <summary>
        /// <c>while &lt;condition&gt; { ... }</c>: a header that tests, a body that branches back to
        /// it, and an end to continue at. The body is emitted as the ordinary block it is, so its
        /// scope — and a shadowing binding's own slot — costs nothing here.
        /// <para>
        /// <b>The condition is emitted inside <c>while.cond</c>, not before the loop.</b> Not
        /// because an expression could have a side effect (none can, which is what
        /// <see cref="EmitBinary"/> leans on) but because <c>i &lt; 10</c> compiles to a load from
        /// <c>i</c>'s slot. Emitted once ahead of the branch it would be a snapshot taken at loop
        /// entry, and the loop would never end.
        /// </para>
        /// <para>
        /// The enclosing function is read <i>before</i> the condition, where <see cref="EmitIf"/>
        /// reads it after — the condition needs a block to live in, so there is no choice. Both are
        /// right: what emission can move is the insert <i>block</i>, never the enclosing function.
        /// </para>
        /// <para>
        /// This is where hoisting allocas to <c>entry</c> stops being tidiness and becomes a
        /// requirement: a <c>let</c> in the body allocates one slot however many times the loop
        /// runs, and the store is what repeats.
        /// </para>
        /// </summary>
        private void EmitWhile(WhileStatement loop)
        {
            var function = _builder.InsertBlock.Parent;

            // Appended in source order so the IR reads in it, the same as an 'if'.
            var condBlock = function.AppendBasicBlock("while.cond");
            var bodyBlock = function.AppendBasicBlock("while.body");
            var endBlock = function.AppendBasicBlock("while.end");

            BranchTo(condBlock);

            _builder.PositionAtEnd(condBlock);
            var condition = EmitExpr(loop.Condition);

            // Built wherever emitting the condition ended up, which is 'while.cond' today and need
            // not be the day a condition takes blocks of its own.
            _builder.BuildCondBr(condition, bodyBlock, endBlock);

            // The body's scope is entered here rather than by the generic block case, because its
            // payload is the two basic blocks and those did not exist until a moment ago. 'Body' is
            // statically a 'BlockStatement', so doing it here is type-safe, and it buys the invariant
            // that whoever enters a scope supplies its data: no half-initialised scope and no pending
            // field for the exit. The three lines of duplication with the block case are the price.
            //
            // 'condBlock' is the header as created, not a re-read of the insert block: a 'continue'
            // has to re-test the whole condition, so it targets where the condition starts.
            _generator._scopes.EnterNew(loop.Body.Kind, (Continue: condBlock, Break: endBlock));

            _builder.PositionAtEnd(bodyBlock);
            EmitStatements(loop.Body.Statements);
            BranchTo(condBlock);

            _generator._scopes.Exit();

            _builder.PositionAtEnd(endBlock);
        }

        /// <summary>
        /// Emits statements in order, stopping at the first one that leaves the block terminated.
        /// What follows a <c>break</c> is unreachable, and appending it after the terminator is what
        /// would make the module fail verification. It is still <i>analyzed</i> — semantic analysis
        /// walks every statement — so unreachable code still reports its own errors; it is only
        /// never emitted.
        /// </summary>
        public void EmitStatements(IReadOnlyList<Statement> statements)
        {
            foreach (var statement in statements)
            {
                if (Terminated)
                    return;
                EmitStatement(statement);
            }
        }

        /// <summary>
        /// Branches to <paramref name="target"/> unless the current block already ends in a
        /// terminator — which it does when the statement just emitted was a <c>break</c> or a
        /// <c>continue</c>. A second terminator in one block is invalid IR, and the branch it
        /// replaces would be unreachable anyway.
        /// </summary>
        private void BranchTo(LLVMBasicBlockRef target)
        {
            if (!Terminated)
                _builder.BuildBr(target);
        }

        /// <summary>Whether the block the builder sits in already ends in a terminator.</summary>
        private bool Terminated => _builder.InsertBlock.Terminator.Handle != IntPtr.Zero;

        /// <summary>
        /// The slot goes in the entry block and the store stays where the binding sits: a frame
        /// slot belongs to the frame, and it is the store — not the alloca — that carries the
        /// binding's position, so initialisation stays in order. A binding that shadows another is
        /// still simply a second alloca, which LLVM gives its own name.
        /// </summary>
        private void EmitLet(LetStatement let)
        {
            var value = EmitExpr(let.Value);
            var type = LlvmType(let.Value.Type
                ?? throw new CodegenException($"binding '{let.Name}' at {let.Position} was never typed"));

            var slot = _allocas.BuildAlloca(type, let.Name);
            _builder.BuildStore(value, slot);
            _generator._scopes.Declare(let.Name, new VariableSlot(slot, type));
        }

        /// <summary>
        /// Both operands are evaluated: no expression can have a side effect yet, so
        /// <c>and</c> and <c>or</c> need no branching. Short-circuiting arrives with
        /// user-defined functions.
        /// </summary>
        private LLVMValueRef EmitBinary(BinaryExpression binary)
        {
            var left = EmitExpr(binary.Left);
            var right = EmitExpr(binary.Right);
            var operandType = binary.Left.Type
                ?? throw new CodegenException($"operand at {binary.Left.Position} was never typed");

            if (operandType == SuruType.F64)
                return EmitFloatBinary(binary.Operator, left, right);

            return binary.Operator switch
            {
                BinaryOperator.Add => _builder.BuildAdd(left, right),
                BinaryOperator.Subtract => _builder.BuildSub(left, right),
                BinaryOperator.Multiply => _builder.BuildMul(left, right),
                // Integer division truncates toward zero and yields an integer.
                BinaryOperator.Divide => _builder.BuildSDiv(left, right),
                BinaryOperator.Remainder => _builder.BuildSRem(left, right),
                BinaryOperator.And => _builder.BuildAnd(left, right),
                BinaryOperator.Or => _builder.BuildOr(left, right),
                _ => _builder.BuildICmp(IntPredicate(binary.Operator), left, right),
            };
        }

        private LLVMValueRef EmitFloatBinary(BinaryOperator op, LLVMValueRef left, LLVMValueRef right) =>
            op switch
            {
                BinaryOperator.Add => _builder.BuildFAdd(left, right),
                BinaryOperator.Subtract => _builder.BuildFSub(left, right),
                BinaryOperator.Multiply => _builder.BuildFMul(left, right),
                BinaryOperator.Divide => _builder.BuildFDiv(left, right),
                BinaryOperator.Remainder => _builder.BuildFRem(left, right),
                BinaryOperator.Equal => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
                BinaryOperator.NotEqual => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, left, right),
                BinaryOperator.Less => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
                BinaryOperator.LessOrEqual => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
                BinaryOperator.Greater => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
                BinaryOperator.GreaterOrEqual => _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
                _ => throw new CodegenException($"cannot apply operator '{Operators.Text(op)}' to 'f64'"),
            };

        private LLVMValueRef EmitUnary(UnaryExpression unary)
        {
            var operand = EmitExpr(unary.Operand);
            return unary.Operator switch
            {
                UnaryOperator.Not => _builder.BuildNot(operand),
                _ when unary.Operand.Type == SuruType.F64 => _builder.BuildFNeg(operand),
                _ => _builder.BuildNeg(operand),
            };
        }

        private void EmitPrintLn(Expression arg)
        {
            var (format, argument) = Render(EmitExpr(arg), TypeOf(arg));
            Printf(format + "\n", argument);
        }

        private void EmitView(ViewDirective view)
        {
            var (format, argument) = Render(EmitExpr(view.Subject), TypeOf(view.Subject));

            WriteFrame(Frame.View,
                ("id", "%d", Int32(view.Id)),
                ("value", format, argument));
        }

        /// <summary>
        /// Compares the two operands and reports the outcome along with both values, so the
        /// driver can say what was expected and what turned up without evaluating anything
        /// itself. The comparison is the one the program computes, not a comparison of the
        /// printed text — <c>%g</c> rounds, and two <c>f64</c>s that print alike need not be
        /// equal.
        /// </summary>
        private void EmitAssert(AssertDirective assert)
        {
            var type = TypeOf(assert.Actual);
            var actual = EmitExpr(assert.Actual);
            var expected = EmitExpr(assert.Expected);

            var equal = type == SuruType.F64
                ? _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, actual, expected)
                : _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, actual, expected);

            // The wire spells an outcome 'pass' or 'fail', so the word is picked at runtime the
            // same way Render picks 'true' or 'false' for a bool.
            var outcome = _builder.BuildSelect(equal, String("pass"), String("fail"));

            var (format, actualArgument) = Render(actual, type);
            var (_, expectedArgument) = Render(expected, type);

            WriteFrame(Frame.Assert,
                ("id", "%d", Int32(assert.Id)),
                ("outcome", "%s", outcome),
                ("actual", format, actualArgument),
                ("expected", format, expectedArgument));

            // TODO: fail-fast. A failing assertion should stop the run here rather than fall
            // through — see "Not yet supported" in doc/testing.md. That means branching on
            // 'equal' after the frame is written: the failing edge sends 'run-finished' and
            // returns from main, so a loop whose assertion fails on its first pass reports
            // once and ends. The sticky rule TestRun applies agrees with it already, since
            // under fail-fast the first failure is the only one.
        }

        /// <summary>
        /// The printf specifier for a value of the given type, and the argument to pass with it.
        /// One source of truth for how a value is rendered, so <c>printLn</c> and the test
        /// records cannot disagree about what a value looks like.
        /// </summary>
        private (string Format, LLVMValueRef Argument) Render(LLVMValueRef value, SuruType type)
        {
            // printf has no bool conversion, so pick the literal text at runtime.
            if (type == SuruType.Bool)
                return ("%s", _builder.BuildSelect(value, String("true"), String("false")));
            if (type == SuruType.I64)
                return ("%lld", value);
            if (type == SuruType.F64)
                return ("%g", value);

            throw new CodegenException($"cannot print a value of type '{type}'");
        }

        /// <summary>
        /// Writes one frame down the test channel: <c>begin</c>, a field per entry, <c>end</c>.
        /// <para>
        /// The <c>kind</c> field is written here rather than by the shim, which has no idea what
        /// a kind is — unlike <see cref="FrameWriter"/>, whose callers do not have to say. It goes
        /// through the same <c>%s</c> path as any other value, so a kind is never a format string.
        /// </para>
        /// <para>
        /// Separate from <see cref="Printf"/>, which is <c>printLn</c>'s alone: a frame goes to the
        /// harness and the program's own output goes to whoever ran it, and the two now travel on
        /// different file descriptors entirely.
        /// </para>
        /// </summary>
        public void WriteFrame(
            string kind, params (string Key, string Format, LLVMValueRef Argument)[] fields)
        {
            if (_generator._mode != BuildMode.Test)
                throw new CodegenException($"a '{kind}' frame has no place in a production build");

            // The empty argument list is typed explicitly for the same reason Printf's is: a
            // collection expression cannot choose between BuildCall2's array and span overloads.
            LLVMValueRef[] none = [];

            _builder.BuildCall2(_generator._frameType, _generator._frameBeginFn, none, "");

            Field(Frame.KindKey, "%s", String(kind));
            foreach (var (key, format, argument) in fields)
                Field(key, format, argument);

            _builder.BuildCall2(_generator._frameType, _generator._frameEndFn, none, "");
        }

        private void Field(string key, string format, LLVMValueRef value)
        {
            LLVMValueRef[] arguments = [String(key), String(format), value];
            _builder.BuildCall2(_generator._fieldType, _generator._fieldFn, arguments, "");
        }

        private void Printf(string format, params LLVMValueRef[] values)
        {
            // The argument array is built and typed explicitly: BuildCall2 also has a
            // ReadOnlySpan overload, and a collection expression cannot choose between them.
            LLVMValueRef[] arguments = [String(format), .. values];
            _builder.BuildCall2(_generator._printfType, _generator._printfFn, arguments, "");
        }

        /// <summary>The generator's interned string, built through this emitter's positioned builder.</summary>
        private LLVMValueRef String(string value) => _generator.String(_builder, value);
    }
}
