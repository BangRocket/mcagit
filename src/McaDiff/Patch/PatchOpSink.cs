using fNbt;
using McaDiff.Diff;
using McaDiff.Nbt;

namespace McaDiff.Patch;

/// <summary>
/// <see cref="IDiffSink"/> that captures applyable <see cref="PatchOp"/>s: typed
/// base/value (added/removed store the whole subtree; arrays store the whole
/// array) encoded losslessly via <see cref="NbtJson"/>.
/// </summary>
public sealed class PatchOpSink : IDiffSink
{
    public List<PatchOp> Ops { get; } = [];

    public void Modified(string path, NbtTag a, NbtTag b)
        => Ops.Add(new PatchOp { Path = path, Base = NbtJson.ToJson(a), Value = NbtJson.ToJson(b) });

    public void TypeChanged(string path, NbtTag a, NbtTag b)
        => Ops.Add(new PatchOp { Path = path, Base = NbtJson.ToJson(a), Value = NbtJson.ToJson(b) });

    public void ArrayChanged(string path, NbtTag a, NbtTag b)
        => Ops.Add(new PatchOp { Path = path, Base = NbtJson.ToJson(a), Value = NbtJson.ToJson(b) });

    public void Added(string path, NbtTag value)
        => Ops.Add(new PatchOp { Path = path, Base = null, Value = NbtJson.ToJson(value) });

    public void Removed(string path, NbtTag value)
        => Ops.Add(new PatchOp { Path = path, Base = NbtJson.ToJson(value), Value = null });
}
