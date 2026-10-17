using Shared.Protocol;
using Shared.Simulation;
using SimMoveKeys = Shared.Simulation.MoveKeys;
using WireMoveKeys = Shared.Protocol.MoveKeys;

namespace Shared.Tests.Protocol;

public class MovementInputMappingTests
{
    [Theory]
    [InlineData(WireMoveKeys.W, SimMoveKeys.W)]
    [InlineData(WireMoveKeys.A, SimMoveKeys.A)]
    [InlineData(WireMoveKeys.S, SimMoveKeys.S)]
    [InlineData(WireMoveKeys.D, SimMoveKeys.D)]
    [InlineData(WireMoveKeys.W | WireMoveKeys.D, SimMoveKeys.W | SimMoveKeys.D)]
    [InlineData(WireMoveKeys.None, SimMoveKeys.None)]
    public void Keys_map_bit_for_bit(WireMoveKeys wire, SimMoveKeys expected)
    {
        var input = MovementInputMapping.FromFrame(new InputFrame { Keys = wire });

        Assert.Equal(expected, input.Keys);
    }

    [Fact]
    public void Facing_is_what_the_server_decodes_from_the_packet()
    {
        var frame = new InputFrame { Sequence = 1, ClientTick = 1, Keys = WireMoveKeys.W, Facing = 12_345 };
        var buffer = new byte[InputPacket.SizeFor(1)];
        InputPacket.Write(buffer, new[] { frame });
        var decoded = new InputFrame[1];
        Assert.True(InputPacket.TryRead(buffer, decoded, out _));

        var predicted = MovementInputMapping.FromFrame(frame);
        var applied = MovementInputMapping.FromFrame(decoded[0]);

        Assert.Equal(applied, predicted);
        Assert.Equal(decoded[0].Facing, predicted.Facing);
    }

    [Fact]
    public void Predicting_the_sent_frame_and_the_received_frame_step_to_the_same_state()
    {
        var frame = new InputFrame { Keys = WireMoveKeys.W | WireMoveKeys.A, Facing = 40_001 };
        var received = frame;
        received.Facing = Quantize.RoundTripAngle(frame.Facing, InputPacket.FacingBits);

        var start = new MovementState(FixVec2.FromCm(10, -20), 0);
        var client = MovementSim.Step(start, MovementInputMapping.FromFrame(frame), false, 400, 20);
        var server = MovementSim.Step(start, MovementInputMapping.FromFrame(received), false, 400, 20);

        Assert.Equal(server, client);
    }
}
