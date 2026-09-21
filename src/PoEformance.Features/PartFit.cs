using System.Numerics;

namespace PoEformance.Features;

/// <summary>How an attachment line's socket was answered.</summary>
/// <remarks>
/// THE FOUR ANSWERS <see cref="MonsterModels"/> GIVES A SOCKET, named so a sweep over the whole
/// table can count them. <see cref="Dropped"/> is the only one that loses a piece outright.
/// </remarks>
public enum PartPlace
{
    /// <summary>The line named no bone: the piece is already in its carrier's space.</summary>
    Root,

    /// <summary>A bone the carrier's rig has, and the piece sits on it.</summary>
    Bone,

    /// <summary>A name neither rig has, on a nested piece: it falls back to its carrier.</summary>
    Astray,

    /// <summary>A name the monster's rig does not have, at the top: the piece is left out.</summary>
    Dropped,
}

/// <summary>What an attachment turned out to be.</summary>
public enum PartKind
{
    /// <summary>No mesh at all - an effect pack or a sound emitter.</summary>
    None,

    /// <summary>A skin whose vertex bones were moved onto its carrier's rig.</summary>
    Skin,

    /// <summary>A skin that could not be retargeted, so it goes rigidly at its socket.</summary>
    Rigid,

    /// <summary>A <c>.fmt</c> prop, which has no rig and goes rigidly at its socket.</summary>
    Prop,

    /// <summary>A mesh or manifest that did not read.</summary>
    Unread,
}

/// <summary>
/// What became of one attachment: where it went, and how much of it found a bone.
/// </summary>
/// <remarks>
/// EVERY PIECE THAT CAME OUT IN THE WRONG PLACE THIS MONTH WAS DIAGNOSED FROM THESE NUMBERS, and
/// every time they were reconstructed by hand out of a text dump of one monster. Named and
/// counted, the same questions can be asked of the whole table at once - see
/// <see cref="ModelSweep"/> - which is the only way the cases nobody has a screenshot of are
/// ever going to turn up.
/// </remarks>
/// <param name="File">The piece's own .ao.</param>
/// <param name="Socket">The socket its line named, as written.</param>
/// <param name="Place">How that socket was answered.</param>
/// <param name="Depth">One for a piece on the monster, two for a piece on a piece.</param>
/// <param name="Kind">What the piece turned out to be.</param>
/// <param name="Bones">Its own rig's bone count, or nought where it brought no rig.</param>
/// <param name="Matched">How many of those found a bone of its carrier's rig.</param>
/// <param name="Strays">
/// How many matched nothing WITHOUT being a root of their own rig, so they took their carrier's
/// root instead. The rig root is left out because it never matches by design - a socketed piece's
/// own root IS its socket, whatever it is called - and counting it would make this true of very
/// nearly every piece there is.
/// </param>
/// <param name="Paired">How many bones the file's own attachment_bones line paired.</param>
/// <param name="Malformed">
/// Whether that line ends in something that is not a bone group this file declares. Tycho's
/// SkirtLayers.ao is the case: the line is a verbatim copy of the bone_group line beneath it.
/// </param>
/// <param name="Paths">How many of its bone names are MERGED-rig paths - <c>a|b|c</c>.</param>
/// <param name="Past">Whether its mesh is indexed past its own rig, so the numbers are its carrier's.</param>
/// <param name="Why">
/// Why a piece did not read, where <see cref="PartKind.Unread"/>; empty otherwise. Without it a
/// corpus can say 113 monsters have an unreadable piece and not whether that is 113 problems or
/// one format seen 113 times - which is exactly the question the first sweep could not answer
/// about itself.
/// </param>
/// <param name="Least">The corner of its box in MODEL space, once everything is on it.</param>
/// <param name="Most">The far corner of the same.</param>
public readonly record struct PartFit(
    string File,
    string Socket,
    PartPlace Place,
    int Depth,
    PartKind Kind,
    int Bones,
    int Matched,
    int Strays,
    int Paired,
    bool Malformed,
    int Paths,
    bool Past,
    string Why,
    Vector3 Least,
    Vector3 Most)
{
    /// <summary>
    /// Whether part of this piece took its carrier's root while the rest of it did not.
    /// </summary>
    /// <remarks>
    /// THE SHAPE THAT SAYS "PART OF IT IS ON THE FLOOR". A bone that matched nothing takes its
    /// carrier's root, and on a "&lt;root&gt;" piece at the top that root is the monster's, which
    /// is the ground between his feet. Bahlak's feather bundle came out as a beam from his chest
    /// to the origin because some of it matched and some of it did not - so it is the MIXTURE
    /// that is the symptom, and a piece where nothing matched is a different case with its own
    /// name.
    /// </remarks>
    public bool Torn => Strays > 0 && Matched > 0;

    /// <summary>Whether the piece was placed at all, or lost on the way.</summary>
    public bool Lost => Place == PartPlace.Dropped || Kind is PartKind.Unread;
}
