using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using MilestoneAdmin.Application.Abstractions;

namespace MilestoneAdmin.Infrastructure
{
    /// <summary>
    /// Stores connection passwords in Windows Credential Manager as generic credentials (per Windows user, protected by
    /// DPAPI, visible and removable in Control Panel > Credential Manager). Nothing is written to application files.
    /// </summary>
    public sealed class CredentialManagerSecretStore : ISecretStore
    {
        private const int CredTypeGeneric = 1;
        private const int CredPersistLocalMachine = 2;
        private const int ErrorNotFound = 1168;

        public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public void Save(string key, string userName, SecureString secret)
        {
            EnsureSupported();
            if (secret == null) throw new ArgumentNullException(nameof(secret));
            var blob = Marshal.SecureStringToGlobalAllocUnicode(secret);
            try
            {
                var cred = new NativeCredential
                {
                    Type = CredTypeGeneric,
                    TargetName = key,
                    UserName = userName ?? string.Empty,
                    CredentialBlob = blob,
                    CredentialBlobSize = secret.Length * 2,
                    Persist = CredPersistLocalMachine,
                    Comment = "XProtect Admin Assistant connection profile"
                };
                if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the password in Windows Credential Manager.");
            }
            finally
            {
                Marshal.ZeroFreeGlobalAllocUnicode(blob);
            }
        }

        public SecureString Load(string key)
        {
            EnsureSupported();
            if (!CredRead(key, CredTypeGeneric, 0, out var ptr))
            {
                var err = Marshal.GetLastWin32Error();
                if (err == ErrorNotFound) return null;
                throw new Win32Exception(err, "Could not read the password from Windows Credential Manager.");
            }

            try
            {
                var cred = Marshal.PtrToStructure<NativeCredential>(ptr);
                var ss = new SecureString();
                var chars = cred.CredentialBlobSize / 2;
                for (var i = 0; i < chars; i++) ss.AppendChar((char)Marshal.ReadInt16(cred.CredentialBlob, i * 2));
                ss.MakeReadOnly();
                return ss;
            }
            finally
            {
                CredFree(ptr);
            }
        }

        public void Delete(string key)
        {
            EnsureSupported();
            if (!CredDelete(key, CredTypeGeneric, 0))
            {
                var err = Marshal.GetLastWin32Error();
                if (err != ErrorNotFound) throw new Win32Exception(err, "Could not delete the password from Windows Credential Manager.");
            }
        }

        private static void EnsureSupported()
        {
            if (!IsSupported) throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref NativeCredential credential, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = false)]
        private static extern void CredFree(IntPtr buffer);
    }

    /// <summary>In-memory secret store (tests and non-Windows development).</summary>
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly System.Collections.Generic.Dictionary<string, SecureString> _items = new System.Collections.Generic.Dictionary<string, SecureString>();

        public void Save(string key, string userName, SecureString secret) => _items[key] = secret.Copy();

        public SecureString Load(string key) => _items.TryGetValue(key, out var s) ? s.Copy() : null;

        public void Delete(string key) => _items.Remove(key);
    }
}
