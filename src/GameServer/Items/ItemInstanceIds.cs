namespace GameServer.Items;

/// <summary>
/// Ids of item instances (spec 4.1 M2 item model: "instances are rows with a unique ID"), made on the world thread without
/// a database round trip: <c>(creator character id &lt;&lt; 24) | seq</c>, where <c>seq</c> counts the instances that character
/// created (<c>characters.item_instance_seq</c>). The counter is written in the same save transaction as the instances it
/// numbered, so a crash rolls both back and no id is given twice (R-7.1-02); an id keeps its creator when the item changes
/// hands later (M4). Deterministic (architecture rule 6).
/// </summary>
public static class ItemInstanceIds
{
    public const int SeqBits = 24;

    /// <summary>Instances one character can ever create (16.7 million).</summary>
    public const long MaxSeq = (1L << SeqBits) - 1;

    /// <summary>Largest character id that fits the other 39 bits of a positive id.</summary>
    public const long MaxCharacterId = (1L << (63 - SeqBits)) - 1;

    public static long Make(long creatorCharacterId, long seq) => (creatorCharacterId << SeqBits) | seq;

    public static long Creator(long instanceId) => instanceId >> SeqBits;

    public static long Seq(long instanceId) => instanceId & MaxSeq;

    /// <summary>
    /// The versioned properties of a new instance (spec 1.3, <c>item_instances.properties</c> v1): enchant level 0 and no
    /// gems (inert until M3), the original owner and an empty owner history (M4).
    /// </summary>
    public static string NewProperties(long originalOwner) =>
        $"{{\"v\":1,\"enchant\":0,\"gems\":[],\"originalOwner\":{originalOwner},\"ownerHistory\":[]}}";
}
