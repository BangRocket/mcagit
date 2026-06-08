using fNbt;

namespace McaGit.Diff;

/// <summary>
/// Receives the leaf-level decisions of the NBT tree walk in
/// <see cref="NbtComparer"/>. The walk handles structure (key union, list
/// identity vs index, recursion); the sink decides how to represent each change —
/// e.g. flattened display rows (<see cref="NbtChangeSink"/>) or applyable patch
/// ops. Added/removed pass the whole subtree; arrays pass both whole arrays.
/// </summary>
public interface IDiffSink
{
    void Added(string path, NbtTag value);
    void Removed(string path, NbtTag value);
    void Modified(string path, NbtTag a, NbtTag b);
    void TypeChanged(string path, NbtTag a, NbtTag b);
    void ArrayChanged(string path, NbtTag a, NbtTag b);
}
