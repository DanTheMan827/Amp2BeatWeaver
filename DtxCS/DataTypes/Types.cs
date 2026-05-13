namespace DtxCS.DataTypes
{
  /// <summary>
  /// Represents the possible types of values in a DataArray.
  /// </summary>
  public enum DataType : int
  {
    INT = 0x00,
    FLOAT = 0x01,
    VARIABLE = 0x02,
    SYMBOL = 0x05,
    EMPTY = 0x06,
    IFDEF = 0x07,
    ELSE = 0x08,
    ENDIF = 0x09,
    ARRAY = 0x10,
    COMMAND = 0x11,
    STRING = 0x12,
    MACRO = 0x13,
    COLOR = 0x15,
    DEFINE = 0x20,
    INCLUDE = 0x21,
    MERGE = 0x22,
    IFNDEF = 0x23,
    AUTORUN = 0x24,
    UNDEF = 0x25
  };

  public abstract class DataNode
  {
    public DataArray Parent { get; set; }
    public abstract string Name { get; }
    public abstract DataType Type { get; }
    public abstract DataNode Evaluate();
    public virtual string ToString(int depth) => ToString();
    public override bool Equals(object obj)
    {
      if (!(obj is DataNode)) return false;
      if ((obj as DataNode).Type != this.Type) return false;
      if (obj.ToString() != this.ToString()) return false;
      return true;
    }
    public override int GetHashCode() => ToString().GetHashCode();
  }
}
