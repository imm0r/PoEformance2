using System.Numerics;
using PoEformance.Core.Schema;

namespace PoEformance.Core.Tests;

/// <summary>
/// Lays a UiElement tree into fake memory, at every offset the schema names.
/// </summary>
/// <remarks>
/// Deliberately built THROUGH THE SCHEMA rather than with hand-written constants: a fixture
/// with its own copy of the offsets would keep passing after the real ones move, which is the
/// one thing tests over a reverse-engineered layout exist to catch.
///
/// Shared rather than private to one test class because two readers now walk this tree - the
/// browser and <c>InterfaceReader</c> - and a second copy of the fixture would be a second place for
/// an offset to be wrong in, with each copy vouching only for itself.
/// </remarks>
internal sealed class UiTree
{
    private const ulong Base = 0x0000_0300_0000_0000;
    private const ulong Strings = 0x0000_0300_1000_0000;
    private const ulong Arrays = 0x0000_0300_2000_0000;

    internal const uint FlagVisible = 0x800;
    internal const uint FlagModifyPos = 0x400;

    private readonly FakeMemoryReader _fake = new();
    private readonly StructDef _ui;
    private ulong _stringCursor = Strings;
    private ulong _arrayCursor = Arrays;

    public UiTree(OffsetSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _ui = schema.Structs["UiElementBase"];
    }

    /// <summary>Element n lives here - spaced far enough apart to hold a whole struct.</summary>
    public static ulong At(int index) => Base + ((ulong)index * 0x1000);

    public FakeMemoryReader Reader => _fake;

    public UiTree Add(
        int index,
        int parent = -1,
        string stringId = "",
        string text = "",
        bool visible = true,
        bool modifiesPosition = false,
        Vector2 relative = default,
        Vector2 size = default,
        byte scaleIndex = 2,
        float multiplier = 1f,
        Vector2 positionModifier = default,
        params int[] children)
    {
        ArgumentNullException.ThrowIfNull(children);
        ulong address = At(index);

        _fake.Place<ulong>(address + (ulong)_ui.OffsetOf("Self"), address);
        _fake.Place<ulong>(address + (ulong)_ui.OffsetOf("ParentPtr"), parent >= 0 ? At(parent) : 0UL);
        _fake.Place<uint>(
            address + (ulong)_ui.OffsetOf("Flags"),
            (visible ? FlagVisible : 0u) | (modifiesPosition ? FlagModifyPos : 0u));
        _fake.Place(address + (ulong)_ui.OffsetOf("ScaleIndex"), scaleIndex);
        _fake.Place(address + (ulong)_ui.OffsetOf("LocalScaleMultiplier"), multiplier);
        PlaceVector(address + (ulong)_ui.OffsetOf("RelativePosition"), relative);
        PlaceVector(address + (ulong)_ui.OffsetOf("UnscaledSize"), size);
        PlaceVector(address + (ulong)_ui.OffsetOf("PositionModifier"), positionModifier);
        PlaceString(address + (ulong)_ui.OffsetOf("StringIdPtr"), stringId);
        PlaceString(address + (ulong)_ui.OffsetOf("TextPtr"), text);

        if (children.Length > 0)
        {
            ulong array = _arrayCursor;
            _arrayCursor += (ulong)(children.Length * 8) + 64;

            for (int i = 0; i < children.Length; i++)
            {
                _fake.Place<ulong>(array + (ulong)(i * 8), At(children[i]));
            }

            _fake.Place<ulong>(address + (ulong)_ui.OffsetOf("ChildrenFirst"), array);
            _fake.Place<ulong>(
                address + (ulong)_ui.OffsetOf("ChildrenLast"), array + (ulong)(children.Length * 8));
        }

        return this;
    }

    private void PlaceVector(ulong address, Vector2 value)
    {
        _fake.Place(address, value.X);
        _fake.Place(address + 4, value.Y);
    }

    private void PlaceString(ulong address, string text)
    {
        if (text.Length == 0)
        {
            _fake.Place(address, new byte[32]); // an empty, valid header
            return;
        }

        _fake.PlaceStdWString(address, text, _stringCursor);
        _stringCursor += 1024;
    }
}
