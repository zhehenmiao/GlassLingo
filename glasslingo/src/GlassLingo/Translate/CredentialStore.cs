using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using GlassLingo.Logging;

namespace GlassLingo.Translate
{
    /// <summary>
    /// Windows 凭据管理器读写。
    ///
    /// 为什么不把 key 写进 config.json：配置文件会被同步、会被截图、
    /// 会被备份到别处。凭据管理器走 DPAPI，跟着当前用户账户加密。
    /// 配置里只留一个引用名（keyring:glasslingo/xxx），真值在这里。
    /// </summary>
    internal static class CredentialStore
    {
        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
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

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite([In] ref CREDENTIAL credential, [In] uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree([In] IntPtr buffer);

        /// <summary>存一个密钥。成功后能立刻读回来验证。</summary>
        public static bool Write(string target, string secret, string userName = "glasslingo")
        {
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("target 为空", nameof(target));
            if (secret == null)
                throw new ArgumentNullException(nameof(secret));

            byte[] blob = Encoding.Unicode.GetBytes(secret);
            IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPtr, blob.Length);

                var cred = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = target,
                    CredentialBlobSize = blob.Length,
                    CredentialBlob = blobPtr,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = userName,
                };

                if (!CredWrite(ref cred, 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Warn($"CredWrite 失败 target={target} err={err}");
                    return false;
                }
                return true;
            }
            finally
            {
                // 明文在托管内存里擦一遍再放，成本几乎为零
                Array.Clear(blob, 0, blob.Length);
                Marshal.FreeHGlobal(blobPtr);
            }
        }

        /// <summary>读密钥。不存在返回 null。</summary>
        public static string? Read(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;

            if (!CredRead(target, CRED_TYPE_GENERIC, 0, out IntPtr ptr))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 1168) // 1168 = ERROR_NOT_FOUND，属于正常情况
                    Log.Warn($"CredRead 失败 target={target} err={err}");
                return null;
            }

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
                if (cred.CredentialBlobSize <= 0 || cred.CredentialBlob == IntPtr.Zero)
                    return "";
                return Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(ptr);
            }
        }

        /// <summary>从 "keyring:xxx" 形式的引用里取出真值。引用无效返回 null。</summary>
        public static string? Resolve(string? apiKeyRef)
        {
            const string prefix = "keyring:";
            if (string.IsNullOrWhiteSpace(apiKeyRef)) return null;
            if (!apiKeyRef.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return Read(apiKeyRef[prefix.Length..].Trim());
        }
    }
}
