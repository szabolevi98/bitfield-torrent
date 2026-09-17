namespace Bitfield.Core.Peers;

/// <summary>
/// What is true of one peer connection at the moment: who is choking whom, who
/// wants what, and which pieces the far end says it has.
///
/// Both sides start out choked and uninterested, which means nothing can be
/// asked for until this client says it is interested and the peer answers by
/// unchoking. That exchange is the whole reason a connection can sit silent for
/// a while before anything moves.
/// </summary>
public sealed class PeerState
{
    private readonly int _pieceCount;

    public PeerState(int pieceCount)
    {
        _pieceCount = pieceCount;
        Available = new PieceBitfield(pieceCount);
    }

    /// <summary>The peer is refusing this client's requests.</summary>
    public bool ChokedByPeer { get; private set; } = true;

    /// <summary>This client is refusing the peer's requests.</summary>
    public bool ChokingPeer { get; private set; } = true;

    /// <summary>This client wants something the peer has.</summary>
    public bool InterestedInPeer { get; private set; }

    /// <summary>The peer wants something this client has.</summary>
    public bool PeerInterested { get; private set; }

    /// <summary>The pieces the peer says it holds.</summary>
    public PieceBitfield Available { get; private set; }

    /// <summary>Whether anything can be asked for right now.</summary>
    public bool CanRequest => !ChokedByPeer && InterestedInPeer;

    /// <summary>
    /// Applies a message that only changes state. Returns false for anything
    /// else — a block, a request — which the caller has to handle itself.
    /// </summary>
    public bool Apply(PeerMessage message)
    {
        switch (message.Id)
        {
            case MessageId.Choke:
                ChokedByPeer = true;
                return true;

            case MessageId.Unchoke:
                ChokedByPeer = false;
                return true;

            case MessageId.Interested:
                PeerInterested = true;
                return true;

            case MessageId.NotInterested:
                PeerInterested = false;
                return true;

            case MessageId.Have:
                int piece = message.ReadHave();
                if ((uint)piece >= (uint)_pieceCount)
                {
                    throw new PeerProtocolException(
                        $"the peer claims piece {piece} of a torrent with {_pieceCount}");
                }

                Available.Set(piece);
                return true;

            case MessageId.Bitfield:
                // A bitfield is only valid as the first message; a peer sending
                // one later would be replacing everything already learned about
                // it, which no client does and a broken one might.
                if (!Available.IsEmpty)
                {
                    throw new PeerProtocolException("the peer sent a bitfield after it had already claimed pieces");
                }

                Available = PieceBitfield.FromBytes(message.Payload.Span, _pieceCount);
                return true;

            default:
                return false;
        }
    }

    public void SetInterested(bool interested) => InterestedInPeer = interested;

    public void SetChoking(bool choking) => ChokingPeer = choking;

    public override string ToString() =>
        $"{(ChokedByPeer ? "choked" : "unchoked")}, "
        + $"{(InterestedInPeer ? "interested" : "not interested")}, {Available}";
}
