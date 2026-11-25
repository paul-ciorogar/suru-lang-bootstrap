using LLVMSharp.Interop;
using Suru.Compiler.Parse.Ast;
using Suru.Compiler.Testing;

namespace Suru.Compiler.Codegen;

public sealed partial class CodeGenerator
{
    /// <summary>
    /// Emits the code of one LLVM function. It owns only what is per-function; everything
    /// per-module stays on the <see cref="CodeGenerator"/>.
    /// <para>
    /// Exists so each function has its own alloca builder, fixed in its own <c>entry</c>.
    /// One builder moved between functions would have to come back to exactly the right spot,
    /// and any slot built while it was away would land silently in the wrong frame.
    /// </para>
    /// <para>
    /// The scope stack is deliberately <i>not</i> per-emitter: the <see cref="ScopeKind.Function"/>
    /// barrier already hides an enclosing function's variables, so a nested body can share it.
    /// </para>
    /// <para>
    /// Nested so it can use the generator's private state without that state being widened, and
    /// disposable so a <see cref="CodegenException"/> does not leak the builders.
    /// </para>
    /// </summary>
    private sealed class FunctionEmitter : IDisposable
    {
        private readonly CodeGenerator _generator;

        private readonly LLVMBuilderRef _builder;

        /// <summary>
        /// Builds every stack slot, so a slot belongs to the frame however deeply branched the
        /// statement that declares it is.
        /// </summary>
        private readonly LLVMBuilderRef _allocas;

        private readonly LLVMValueRef _function;
        private readonly LLVMBasicBlockRef _body;

        public FunctionEmitter(CodeGenerator generator, LLVMValueRef function)
        {
            _generator = generator;
            _function = function;

            var context = generator._llvmModule.Context;
            _builder = LLVMBuilderRef.Create(context);
            _allocas = LLVMBuilderRef.Create(context);

            // 'entry' holds nothing but allocas and 'body' holds the code, so no branch emitted
            // later can terminate the block the allocas still go into.
            var entry = function.AppendBasicBlock("entry");
            _body = function.AppendBasicBlock("body");

            _allocas.PositionAtEnd(entry);
            _builder.PositionAtEnd(_body);
        }

        /// <summary>
        /// Closes <c>entry</c>. Must come last: a slot built afterwards would sit behind the
        /// terminator.
        /// </summary>
        public void Finish() => _allocas.BuildBr(_body);

        public void Dispose()
        {
            _allocas.Dispose();
            _builder.Dispose();
        }

        public void Return(LLVMValueRef value) => _builder.BuildRet(value);

        /// <summary>
        /// A <c>fn</c>'s body, parameters included.
        /// <para>
        /// A parameter is bound to a stack slot rather than to the incoming value, so it can be
        /// read, assigned and <c>#mock</c>ed exactly like a <c>let</c>. The slot is named
        /// <c>a.addr</c> so it reads apart from the incoming <c>%a</c> in the IR.
        /// </para>
        /// <para>
        /// Parameters are bound before the body's functions because that is the analyzer's order,
        /// and the two stages must agree on what each name in the body means.
        /// </para>
        /// </summary>
        public void EmitBody(FunctionDeclaration declaration)
        {
            _generator._scopes.EnterNew(declaration.Body.Kind);

            for (var i = 0; i < declaration.Parameters.Count; i++)
            {
                var parameter = declaration.Parameters[i];
                var type = LlvmType(parameter.Type
                    ?? throw new CodegenException($"parameter '{parameter.Name}' at {parameter.Position} was never typed"));

                var slot = _allocas.BuildAlloca(type, parameter.Name + ".addr");
                _builder.BuildStore(_function.GetParam((uint)i), slot);
                _generator._scopes.Declare(parameter.Name, new VariableSlot(slot, type));
            }

            _generator.EmitFunctions(declaration.Body.Statements);
            EmitStatements(declaration.Body.Statements);
            TerminateFunction(declaration);

            _generator._scopes.Exit();
        }

        /// <summary>
        /// Ends a body whose last block is still open: a <c>void</c> function returns, and any
        /// other is marked <c>unreachable</c>, since the analyzer proved every path returns.
        /// <para>
        /// Exists because the open block is real — when both arms of an <c>if</c> return,
        /// <c>if.end</c> is still there, with no terminator, and the IR is invalid without one.
        /// It is also the one place where a mistake in the analyzer's <c>AlwaysReturns</c> becomes
        /// a miscompile rather than a rejected program, so the two must agree on what returns.
        /// </para>
        /// </summary>
        private void TerminateFunction(FunctionDeclaration declaration)
        {
            if (Terminated)
                return;

            if (declaration.ReturnType == SuruType.Void)
                _builder.BuildRetVoid();
            else
                _builder.BuildUnreachable();
        }

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
                    // A block is only a scope; any basic blocks belong to the 'if' around it. A
                    // loop body is the exception and enters its own scope in EmitWhile, because
                    // its scope data does not exist until the loop's blocks do.
                    _generator._scopes.EnterNew(block.Kind);
                    _generator.EmitFunctions(block.Statements);
                    EmitStatements(block.Statements);
                    _generator._scopes.Exit();
                    break;
                case FunctionDeclaration:
                    // Already emitted when its scope was entered; see EmitFunctions.
                    break;
                case ReturnStatement returned:
                    // Leaving the block terminated is all a 'return' has to do: the handling
                    // 'break' needed for that already covers a 'return' anywhere in a body.
                    if (returned.Value is null)
                        _builder.BuildRetVoid();
                    else
                        _builder.BuildRet(EmitExpr(returned.Value));
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
                    // A mock is an assignment; only the lexer's build mode makes it test-only.
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
        /// An expression's value, or a null value reference for one of type
        /// <see cref="SuruType.Void"/>.
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
                case CallExpression call:
                    return EmitCall(call);
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
        /// <c>if</c>, with or without an <c>else</c>. With no <c>else</c> there is still an
        /// <c>if.end</c>, because the program needs somewhere to continue.
        /// <para>
        /// An arm can leave early, so when both do, <c>if.end</c> has no predecessors. That is
        /// valid IR only while no phi node exists — every variable is an alloca in <c>entry</c>.
        /// The first expression that needs a phi, a short-circuiting <c>and</c>, must revisit it.
        /// </para>
        /// </summary>
        private void EmitIf(IfStatement branch)
        {
            // Read after the condition, so it would still be right if the condition emitted
            // blocks of its own, as a short-circuiting 'and' will.
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

            // 'else if' is an IfStatement in the else arm and needs no case of its own.
            if (otherwise is not null)
            {
                _builder.PositionAtEnd(elseBlock);
                EmitStatement(otherwise);
                BranchTo(endBlock);
            }

            _builder.PositionAtEnd(endBlock);
        }

        /// <summary>
        /// <c>while &lt;condition&gt; { ... }</c>.
        /// <para>
        /// The condition belongs inside the loop, not before it: <c>i &lt; 10</c> reads
        /// <c>i</c>'s slot, and evaluated once at entry it would never change and the loop would
        /// never end.
        /// </para>
        /// <para>
        /// The loop is why allocas live in <c>entry</c> rather than where they are declared: a
        /// <c>let</c> in the body must be one slot however many times the body runs.
        /// </para>
        /// </summary>
        private void EmitWhile(WhileStatement loop)
        {
            // Read before the condition, unlike EmitIf, because the condition needs a block to go
            // in. Either way is right: emission can change the block, never the function.
            var function = _builder.InsertBlock.Parent;

            var condBlock = function.AppendBasicBlock("while.cond");
            var bodyBlock = function.AppendBasicBlock("while.body");
            var endBlock = function.AppendBasicBlock("while.end");

            BranchTo(condBlock);

            _builder.PositionAtEnd(condBlock);
            var condition = EmitExpr(loop.Condition);
            _builder.BuildCondBr(condition, bodyBlock, endBlock);

            // Entered here rather than by the block case, because the scope's data is the loop's
            // exits and they exist only now. 'continue' targets the start of the condition, so
            // the whole condition is tested again.
            _generator._scopes.EnterNew(loop.Body.Kind, (Continue: condBlock, Break: endBlock));

            _builder.PositionAtEnd(bodyBlock);
            _generator.EmitFunctions(loop.Body.Statements);
            EmitStatements(loop.Body.Statements);
            BranchTo(condBlock);

            _generator._scopes.Exit();

            _builder.PositionAtEnd(endBlock);
        }

        /// <summary>
        /// Emits statements up to the first one that leaves the block terminated. What follows is
        /// unreachable, and emitting it after a terminator would fail verification. It has still
        /// been analyzed, so unreachable code still reports its own errors.
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
        /// A branch that is skipped when the block has already left by a <c>break</c>,
        /// <c>continue</c> or <c>return</c>. A second terminator is invalid IR, and the branch
        /// would be unreachable anyway.
        /// </summary>
        private void BranchTo(LLVMBasicBlockRef target)
        {
            if (!Terminated)
                _builder.BuildBr(target);
        }

        private bool Terminated => _builder.InsertBlock.Terminator.Handle != IntPtr.Zero;

        /// <summary>
        /// The slot belongs to the frame, but the store stays where the <c>let</c> is written, so
        /// bindings are initialised in source order. A shadowing binding is simply another slot.
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
        /// Both operands are evaluated, <c>and</c> and <c>or</c> included.
        /// <para>
        /// TODO(short-circuit): that was sound while no expression could have a side effect, and a
        /// call is one — <c>false and f()</c> runs <c>f</c> today. Task 7 of
        /// doc/RD/todo_functions.md fixes it.
        /// </para>
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

        /// <summary>
        /// A call to a declared function, arguments evaluated left to right. A <c>void</c> call
        /// is left unnamed, because naming an instruction with no value is invalid IR.
        /// </summary>
        private LLVMValueRef EmitCall(CallExpression call)
        {
            var function = _generator.Function(call.Name);

            // Typed explicitly: BuildCall2 also has a span overload, and a collection expression
            // cannot choose between them.
            LLVMValueRef[] arguments = [.. call.Args.Select(EmitExpr)];

            if (TypeOf(call) == SuruType.Void)
            {
                _builder.BuildCall2(function.Type, function.Function, arguments, "");
                return default;
            }

            return _builder.BuildCall2(function.Type, function.Function, arguments, call.Name);
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
        /// Reports the outcome with both values, so the driver can say what was expected and what
        /// turned up without evaluating anything itself. The program does the comparison, not the
        /// driver on printed text: <c>%g</c> rounds, and two <c>f64</c>s that print alike need not
        /// be equal.
        /// </summary>
        private void EmitAssert(AssertDirective assert)
        {
            var type = TypeOf(assert.Actual);
            var actual = EmitExpr(assert.Actual);
            var expected = EmitExpr(assert.Expected);

            var equal = type == SuruType.F64
                ? _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, actual, expected)
                : _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, actual, expected);

            // The wire spells an outcome 'pass' or 'fail'.
            var outcome = _builder.BuildSelect(equal, String("pass"), String("fail"));

            var (format, actualArgument) = Render(actual, type);
            var (_, expectedArgument) = Render(expected, type);

            WriteFrame(Frame.Assert,
                ("id", "%d", Int32(assert.Id)),
                ("outcome", "%s", outcome),
                ("actual", format, actualArgument),
                ("expected", format, expectedArgument));

            // TODO: fail-fast. A failing assertion should end the run here rather than fall
            // through — see "Not yet supported" in doc/testing.md — so a loop whose assertion
            // fails on its first pass reports once. TestRun's sticky rule already agrees, since
            // under fail-fast the first failure is the only one.
        }

        /// <summary>
        /// The printf specifier for a value of the given type, and the argument to pass with it.
        /// One source of truth, so <c>printLn</c> and the test frames cannot disagree about what a
        /// value looks like.
        /// </summary>
        private (string Format, LLVMValueRef Argument) Render(LLVMValueRef value, SuruType type)
        {
            // printf has no bool conversion.
            if (type == SuruType.Bool)
                return ("%s", _builder.BuildSelect(value, String("true"), String("false")));
            if (type == SuruType.I64)
                return ("%lld", value);
            if (type == SuruType.F64)
                return ("%g", value);

            throw new CodegenException($"cannot print a value of type '{type}'");
        }

        /// <summary>
        /// One frame down the test channel.
        /// <para>
        /// The <c>kind</c> is written here because the shim does not know what a kind is, and as
        /// a <c>%s</c> value so a kind is never a format string.
        /// </para>
        /// <para>
        /// Kept apart from <see cref="Printf"/>, which is <c>printLn</c>'s alone: frames go to the
        /// harness and the program's own output to whoever ran it.
        /// </para>
        /// </summary>
        public void WriteFrame(
            string kind, params (string Key, string Format, LLVMValueRef Argument)[] fields)
        {
            if (_generator._mode != BuildMode.Test)
                throw new CodegenException($"a '{kind}' frame has no place in a production build");

            // Typed explicitly for the same reason as in EmitCall.
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
            // Typed explicitly for the same reason as in EmitCall.
            LLVMValueRef[] arguments = [String(format), .. values];
            _builder.BuildCall2(_generator._printfType, _generator._printfFn, arguments, "");
        }

        private LLVMValueRef String(string value) => _generator.String(_builder, value);
    }
}
