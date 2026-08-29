# Remote connections and credentials

Odyssey currently supports SFTP browsing and local↔SFTP queued copy with password authentication.

- Host identity verification is strict. A connection without an expected SHA-256 host-key fingerprint is rejected while reporting the presented fingerprint. Verify it out of band, enter the exact fingerprint, and reconnect.
- Passwords are session-only. They are cleared from the view model after connection and are not written to preferences, logs, audit, or durable queue JSON.
- Transfer jobs store only a session connection key. After an application restart the user must reconnect before remote work can continue.
- Upload/download stream through temporary destination names and publish after success; links are not silently followed.
- Quick View streams one bounded block directly over the connected session. It keeps no local preview file or durable cache, rejects non-canonical paths and reported links, and revalidates remote size/modification time around reads. Disconnect closes a remote preview.

Private keys, encrypted keys, SSH agent, keyboard-interactive authentication, secure credential vault storage, persistent known-host management, true partial-file resume, proxy/jump hosts, FTP/FTPS, WebDAV, SMB, S3, and remote→remote transfer are not implemented. There is no certificate or host-key “ignore all” option.
