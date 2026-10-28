# VoiceBridge room protocol 3

HTTPS POST `/api/rooms` allows anonymous creation when PUBLIC_ROOM_CREATION=1, subject to rate/capacity limits. Otherwise it requires a private sender key as Bearer and compares its SHA256 to the configured hash. Returns random room id, owner writeToken and listenToken. Owner DELETE `/api/rooms/:id` revokes the room. Room crypto root key is generated on the host and never posted to the server.

Invitation: `/room/<32 hex>#<64 hex listenToken>.<64 hex rootKey>`.

Private desktop control page appends `.desktop.<32 hex memberId>.<64 hex controlToken>`. A new controller displaces the previous controller and mutes the desktop microphone. Fragment `silent` is an optional local initial receive mute, omitted from copied invitations.

WebSocket `/api/rooms/:id/voice`: first JSON `{type:"join",version:3,role:"desktop"|"browser",token,name,hostToken?}`. Desktop welcome includes private controlToken. Controller joins with role/controller, memberId/controlToken instead of a room token. Server validates exact HTTP Origin, room capacity, lease and message rates.

Audio kind 0=mono voice, 1=stereo music, 48 kHz/960 frames. Raw Opus packets have no Ogg container. Packet VBA2: ASCII magic 4 bytes; kind 1; uint32 little-endian sequence; random 12-byte GCM nonce; ciphertext; 16-byte tag. Server fanout prepends 16-byte sender member id, never trusts client-supplied identity, excludes the sender and controller sockets. Per-kind sequences must increase.

HKDF-SHA256 root key, salt UTF8 room id, info `VoiceBridge audio v2|<kind>|<memberId>`; AES-256-GCM AAD `<room>|<kind>|<memberId>|<sequence>`.

State/control envelope has type, connectionId, sequence, iv (base64 nonce), data (base64 ciphertext+tag). HKDF info `VoiceBridge state v2|<memberId>|<connectionId>` or `VoiceBridge control v2|<memberId>|<connectionId>`; AAD `<room>|<direction>|<memberId>|<connectionId>|<sequence>`. Controller epochs prevent cross-connection replay. Device names and source inventory go only between the desktop and its private controller. Public member flags expose nickname, microphone/music state and selected source display name only.

Full configure commands carry a monotonically increasing revision. Desktop echoes appliedRevision so a periodic state update cannot revert a slider still awaiting acknowledgement. Invalid packets and stale audio are dropped; slow connections are closed rather than accumulating unlimited latency.

Version 0.9.1: JSON `{type:"rename",name}` changes only the authenticated browser peer or the controller's own desktop member; it cannot target other member ids. Identity-stable member updates do not trigger join/leave cues. New room entry opens the microphone; first desktop controller preserves that state, while replacing/closing the controller still mutes for privacy.

Version 0.10.0 adds optional screen signaling on the same authenticated text WebSocket. Existing VBA2 voice/music packet format is unchanged; no video bytes enter that path. Only browser peers and the current paired controller are attached to screen signaling. A native desktop socket cannot publish video or obtain screen ICE credentials on its own.

Server `screen:hello` contains screen protocol `version:1`, ICE server configuration, expiry (Unix seconds) and `relayAvailable`. Clients request `screen:ice` every four minutes; TURN usernames expire after ten minutes and are HMAC-SHA1 signed by a server-only shared secret. No long-lived TURN credentials are shipped to clients.

Client `screen:claim` has a caller requestId (1–32 alphanumeric/hyphen characters), mode (`smooth|balanced|clear`) and surface (`window|monitor|browser`). An exclusive per-room reservation yields `screen:claimed` with a random 32-hex session. A busy room returns `screen:error` with code `busy`. `screen:state` announces owner/name/session, pause/mode/surface, viewer member IDs and per-link `maxBitrate`; owner null means no presentation. Total video budget is divided across active presentations and viewers.

`screen:watch` with the current session registers a viewer; optional `restart:true` rotates its random link ID. The viewer receives `screen:watching` before the presenter receives `screen:viewer` (viewer/link/joined). `screen:signal` carries session/link/to and either a video-only SDP description (offer from presenter, answer from viewer, ≤24000 characters) or 1–16 ICE candidates. The server derives from-member identity from the authenticated socket, verifies the active presenter-viewer pairing and link, and rejects foreign-room or stale signaling. Candidate strings are bounded; signaling has its own 120-message/sec budget and does not consume the audio-control allowance.

Only the current presenter may `screen:update` (session, paused, mode, surface) or `screen:stop` (session). `screen:unwatch` removes the caller's viewer link. Presenter/controller replacement, disconnect, room close or lease expiry releases associated reservations. Video uses WebRTC DTLS-SRTP; the trusted signaling server sees SDP/ICE. Shared application audio continues through music kind 1 and is never duplicated into the video peer connections.
