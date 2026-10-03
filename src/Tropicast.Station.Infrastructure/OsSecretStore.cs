using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Infrastructure;

/// <summary>Native credential stores. Linux uses libsecret's secret-tool with secrets on stdin only.</summary>
public sealed class OsSecretStore : ISecretStore
{
    public Task<string?> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        => ExecuteAsync(profileId, null, Operation.Get, cancellationToken);

    public async Task SetAsync(Guid profileId, string password, CancellationToken cancellationToken = default)
    {
        if (!ProfileValidator.IsValidPassword(password))
        {
            throw new ArgumentException("Invalid password.", nameof(password));
        }

        await ExecuteAsync(profileId, password, Operation.Set, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid profileId, CancellationToken cancellationToken = default)
        => await ExecuteAsync(profileId, null, Operation.Delete, cancellationToken).ConfigureAwait(false);

    private static Task<string?> ExecuteAsync(Guid id, string? password, Operation operation, CancellationToken cancellationToken)
    {
        var key = $"Tropicast.Station/{id:D}";
        if (OperatingSystem.IsLinux())
        {
            return LinuxAsync(key, password, operation, cancellationToken);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                return Windows(key, password, operation);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Mac(key, password, operation);
            }

            throw new SecretStoreException("No secure credential store is supported on this OS.");
        }, cancellationToken);
    }

    private static async Task<string?> LinuxAsync(string key, string? password, Operation operation, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(operation switch { Operation.Get => "lookup", Operation.Set => "store", _ => "clear" });
        if (operation == Operation.Set)
        {
            start.ArgumentList.Add("--label=Tropicast Station source password");
        }

        start.ArgumentList.Add("application");
        start.ArgumentList.Add("Tropicast.Station");
        start.ArgumentList.Add("profile");
        start.ArgumentList.Add(key);
        using var process = new Process { StartInfo = start };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var started = false;
        try
        {
            process.Start();
            started = true;
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            if (password is not null)
            {
                await process.StandardInput.WriteAsync(password.AsMemory(), deadline.Token).ConfigureAwait(false);
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            var stderr = await error.ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                return operation == Operation.Get ? result.TrimEnd('\r', '\n') : null;
            }

            if (process.ExitCode == 1 && stderr.Length == 0 && operation != Operation.Set)
            {
                return null; // libsecret reports "not found" with exit 1 and no diagnostic.
            }

            throw new SecretStoreException("libsecret could not access the keyring. Unlock it and retry.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SecretStoreException("Install libsecret's secret-tool and run an unlocked Secret Service keyring (e.g. GNOME Keyring).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SecretStoreException("The keyring did not respond within 60 seconds. Unlock it and retry.");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string? Windows(string key, string? password, Operation operation)
    {
        if (operation == Operation.Get)
        {
            if (!Native.CredRead(key, 1, 0, out var pointer))
            {
                if (Marshal.GetLastWin32Error() == 1168)
                {
                    return null;
                }

                throw new SecretStoreException("Windows Credential Manager refused access.");
            }

            try
            {
                var credential = Marshal.PtrToStructure<Native.Credential>(pointer);
                return Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
            }
            finally
            {
                Native.CredFree(pointer);
            }
        }

        if (operation == Operation.Delete)
        {
            if (!Native.CredDelete(key, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            {
                throw new SecretStoreException("Windows Credential Manager could not delete the password.");
            }

            return null;
        }

        var blob = Marshal.StringToCoTaskMemUni(password!);
        try
        {
            var credential = new Native.Credential
            {
                Type = 1, TargetName = key, BlobSize = (uint)(password!.Length * 2),
                Blob = blob, Persist = 2, UserName = "source",
            };
            if (!Native.CredWrite(ref credential, 0))
            {
                throw new SecretStoreException("Windows Credential Manager could not save the password.");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }

        return null;
    }

    private static string? Mac(string key, string? password, Operation operation)
    {
        var service = Encoding.UTF8.GetBytes("Tropicast.Station");
        var account = Encoding.UTF8.GetBytes(key);
        var status = Native.SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service,
            (uint)account.Length, account, out var length, out var data, out var item);
        if (status != 0 && status != -25300)
        {
            throw new SecretStoreException("macOS Keychain refused access. Unlock the login keychain and retry.");
        }

        try
        {
            if (operation == Operation.Get)
            {
                return status == -25300 ? null : Marshal.PtrToStringUTF8(data, (int)length);
            }

            if (operation == Operation.Delete)
            {
                if (status == 0 && Native.SecKeychainItemDelete(item) != 0)
                {
                    throw new SecretStoreException("macOS Keychain could not delete the password.");
                }

                return null;
            }

            var bytes = Encoding.UTF8.GetBytes(password!);
            try
            {
                var result = status == 0
                    ? Native.SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes)
                    : Native.SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                        (uint)account.Length, account, (uint)bytes.Length, bytes, IntPtr.Zero);
                if (result != 0)
                {
                    throw new SecretStoreException("macOS Keychain could not save the password.");
                }
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }

            return null;
        }
        finally
        {
            if (status == 0)
            {
                _ = Native.SecKeychainItemFreeContent(IntPtr.Zero, data);
                Native.CFRelease(item);
            }
        }
    }

    private enum Operation { Get, Set, Delete }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            internal uint Flags;
            internal uint Type;
            internal string TargetName;
            internal string? Comment;
            internal long LastWritten;
            internal uint BlobSize;
            internal IntPtr Blob;
            internal uint Persist;
            internal uint AttributeCount;
            internal IntPtr Attributes;
            internal string? TargetAlias;
            internal string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(IntPtr credential);

        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        [DllImport(Security)]
        internal static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, out IntPtr item);

        [DllImport(Security)]
        internal static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);

        [DllImport(Security)]
        internal static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);

        [DllImport(Security)]
        internal static extern int SecKeychainItemDelete(IntPtr item);

        [DllImport(Security)]
        internal static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        internal static extern void CFRelease(IntPtr item);
    }
}
