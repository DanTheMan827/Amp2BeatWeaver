using System;
using System.Collections.Generic;
using DtxCS.DataTypes;

namespace DtxCS
{
  static class Builtins
  {
    public static Dictionary<DataSymbol, Func<DataCommand, DataNode>> Funcs { get; }

    static Builtins()
    {
      Funcs = new Dictionary<DataSymbol, Func<DataCommand, DataNode>>();
      Funcs.Add(DataSymbol.Symbol("abs"),        Abs);
      Funcs.Add(DataSymbol.Symbol("+"),          Add);
      Funcs.Add(DataSymbol.Symbol("+="),         AddEq);
      Funcs.Add(DataSymbol.Symbol("&"),          BitAnd);
      Funcs.Add(DataSymbol.Symbol("append_str"), AppendStr);
      Funcs.Add(DataSymbol.Symbol("assign"),     Assign);
      Funcs.Add(DataSymbol.Symbol("clamp"),      Clamp);
      Funcs.Add(DataSymbol.Symbol("--"),         Dec);
      Funcs.Add(DataSymbol.Symbol("/"),          Divide);
      Funcs.Add(DataSymbol.Symbol("="),          Eq);
      Funcs.Add(DataSymbol.Symbol("++"),         Inc);
      Funcs.Add(DataSymbol.Symbol(">"),          Gt);
      Funcs.Add(DataSymbol.Symbol("<"),          Lt);
      Funcs.Add(DataSymbol.Symbol("-"),          Subtract);
      Funcs.Add(DataSymbol.Symbol("if"),         If);
    }

    static DataNode Abs(DataCommand i)      { var a = i.EvalAll(); return new DataAtom(Math.Abs(a.Number(1))); }
    static DataNode Add(DataCommand i)
    {
      var a = i.EvalAll();
      if (a.Node(1).Type == DataType.INT && a.Node(2).Type == DataType.INT)
        return new DataAtom(a.Int(1) + a.Int(2));
      return new DataAtom(a.Number(1) + a.Number(2));
    }
    static DataNode AddEq(DataCommand i)    { var a = i.EvalAll(); return new DataAtom(a.Number(1) + a.Number(2)); }
    static DataNode BitAnd(DataCommand i)   { var a = i.EvalAll(); return new DataAtom(a.Int(1) & a.Int(2)); }
    static DataNode AppendStr(DataCommand i){ var a = i.EvalAll(); return new DataAtom(a.String(1) + a.String(2)); }
    static DataNode Assign(DataCommand i)   { i.Var(1).Value = i.Node(2).Evaluate(); return i.Var(1).Value; }
    static DataNode Clamp(DataCommand i)
    {
      var a = i.EvalAll();
      float f1 = a.Number(1), f2 = a.Number(2), f3 = a.Number(3);
      return new DataAtom(f1 > f3 ? f3 : f1 < f2 ? f1 : f2);
    }
    static DataNode Dec(DataCommand i)
    {
      var a = i.Var(1).Value as DataAtom;
      i.Var(1).Value = new DataAtom(a.Int - 1);
      return i.Var(1).Value;
    }
    static DataNode Divide(DataCommand i)   { var a = i.EvalAll(); return new DataAtom(a.Number(1) / a.Number(2)); }
    static DataNode Eq(DataCommand i)       { var a = i.EvalAll(); return new DataAtom(a.Node(1) == a.Node(2) ? 1 : 0); }
    static DataNode Gt(DataCommand i)       { var a = i.EvalAll(); return new DataAtom(a.Number(1) > a.Number(2) ? 1 : 0); }
    static DataNode Inc(DataCommand i)
    {
      var a = i.Var(1).Value as DataAtom;
      i.Var(1).Value = new DataAtom(a.Int + 1);
      return i.Var(1).Value;
    }
    static DataNode Lt(DataCommand i)       { var a = i.EvalAll(); return new DataAtom(a.Number(1) < a.Number(2) ? 1 : 0); }
    static DataNode Subtract(DataCommand i)
    {
      var a = i.EvalAll();
      if (a.Node(1).Type == DataType.INT && a.Node(2).Type == DataType.INT)
        return new DataAtom(a.Int(1) - a.Int(2));
      return new DataAtom(a.Number(1) - a.Number(2));
    }
    static DataNode If(DataCommand i)
    {
      if ((i.Children[1].Evaluate() as DataAtom).Int == 0)
        return i.Children[3].Evaluate();
      return i.Children[2].Evaluate();
    }
  }
}
