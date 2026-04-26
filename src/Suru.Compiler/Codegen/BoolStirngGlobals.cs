// Covered fixtures: print, exit_test, print-error, negative-literals, arithmetic, comparisons, control-flow, fibonacci, while-loop, strings, include-test, file_io, file_io_write, arrays
using System.Text;

namespace Suru.Compiler.Codegen;

internal class BoolStirngGlobals
{
    private StringBuilder _sb;
    private bool _FmtFloatAdded  = false;
    private bool _FmtIntAdded    = false;
    private bool _FmtIntRawAdded = false;
    private bool _FmtInt32Added  = false;
    private bool _StrFalseAdded  = false;
    private bool _StrTrueAdded   = false;
    private bool _FmtSAdded      = false;
    private bool _ModeRAdded     = false;
    private bool _ModeWAdded     = false;

    public BoolStirngGlobals()
    {
        _sb = new StringBuilder();
    }

    public override string ToString() => _sb.ToString();

    public void AddFmtS() {
        if (_FmtSAdded) return;
        _sb.AppendLine("@.fmt_s = private unnamed_addr constant [4 x i8] c\"%s\\0A\\00\""); 
        _FmtSAdded = true;
    }
    
    public void AddStrTrue() {   
        if (_StrTrueAdded) return;
        _sb.AppendLine("@.str_true = private unnamed_addr constant [5 x i8] c\"true\\00\"");
        _StrTrueAdded = true;
    }
    public void AddStrFalse() {  
        if (_StrFalseAdded) return;
        _sb.AppendLine("@.str_false = private unnamed_addr constant [6 x i8] c\"false\\00\"");
        _StrFalseAdded = true;
    }
    public void AddFmtInt32() {  
        if (_FmtInt32Added) return;
        _sb.AppendLine("@.fmt_int32 = private unnamed_addr constant [4 x i8] c\"%d\\0A\\00\"");
        _FmtInt32Added = true;
    }
    public void AddFmtInt() {    
        if (_FmtIntAdded) return;
        _sb.AppendLine("@.fmt_int = private unnamed_addr constant [6 x i8] c\"%lld\\0A\\00\"");
        _FmtIntAdded = true;
    }
    public void AddFmtFloat() {
        if (_FmtFloatAdded) return;
        _sb.AppendLine("@.fmt_float = private unnamed_addr constant [7 x i8] c\"%.15g\\0A\\00\"");
        _FmtFloatAdded = true;
    }

    // No trailing newline — used by EmitInt64ToString to format digits into a Seq data buffer.
    public void AddFmtIntRaw() {
        if (_FmtIntRawAdded) return;
        _sb.AppendLine("@.fmt_int_raw = private unnamed_addr constant [5 x i8] c\"%lld\\00\"");
        _FmtIntRawAdded = true;
    }

    // fopen mode strings — raw i8* constants, not Seq-wrapped Suru Strings.
    public void AddModeR() {
        if (_ModeRAdded) return;
        _sb.AppendLine("@.mode_r = private unnamed_addr constant [2 x i8] c\"r\\00\"");
        _ModeRAdded = true;
    }

    public void AddModeW() {
        if (_ModeWAdded) return;
        _sb.AppendLine("@.mode_w = private unnamed_addr constant [2 x i8] c\"w\\00\"");
        _ModeWAdded = true;
    }
}