using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mono.Unix;
using Mono.Unix.Native;

namespace MediaBrowser.IsoMounter
{
	public class LinuxIsoManager : IIsoMounter
	{
		private readonly SemaphoreSlim _mountSemaphore = new SemaphoreSlim(3, 3);

		private readonly string _tmpPath;
		private readonly string _mountELF;
		private readonly string _umountELF;
		private readonly string _sudoELF;

		private readonly ILogger _logger;

		public LinuxIsoManager(ILogger logger, IHttpClient httpClient, IApplicationPaths appPaths, IZipClient zipClient)
		{
			_logger = logger;
			_tmpPath = Path.DirectorySeparatorChar + "tmp" + Path.DirectorySeparatorChar + "mediabrowser";
			_mountELF = Path.DirectorySeparatorChar + "usr" + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar + "mount";
			_umountELF = Path.DirectorySeparatorChar + "usr" + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar + "umount";
			_sudoELF = Path.DirectorySeparatorChar + "usr" + Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar + "sudo";
		}

		public string Name
		{
			get { return "LinuxMount"; }
		}

		public bool RequiresInstallation
		{
			get
			{
				return false;
			}
		}

		public bool IsInstalled
		{
			get
			{
				return true;
			}
		}

		public Task Install(CancellationToken cancellationToken)
		{
			//TODO Clean up task(Remove mount point from previous mb3 run)
			return Task.FromResult(false);
		}

		public async Task<IIsoMount> Mount(string isoPath, CancellationToken cancellationToken)
		{
			if (string.IsNullOrEmpty(isoPath))
			{
				throw new ArgumentNullException("isoPath");
			}

			if (!(File.Exists(_mountELF) && File.Exists(_umountELF) && File.Exists(_sudoELF)))
			{
				throw new IOException("Missing mount, umount or sudo. Unable to continue");
			}

			string mountFolder = Path.Combine(_tmpPath, Guid.NewGuid().ToString());

			_logger.Debug("Creating mount point {0}", mountFolder);
			try
			{
				Directory.CreateDirectory(mountFolder);
			}
			catch (UnauthorizedAccessException)
			{
				throw new IOException("Unable to create mount point(Permission denied) for " + isoPath);
			}
			catch (Exception)
			{
				throw new IOException("Unable to create mount point for " + isoPath);
			}

			_logger.Info("Mounting {0}...", isoPath);

			await _mountSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

			string cmdFilename = _sudoELF;
			string cmdArguments = string.Format("\"{0}\" \"{1}\" \"{2}\"", _mountELF, isoPath, mountFolder);

			if (Syscall.getuid() == 0)
			{
				cmdFilename = _mountELF;
				cmdArguments = string.Format("\"{0}\" \"{1}\"", isoPath, mountFolder);
			}

			var process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					FileName = cmdFilename,
					Arguments = cmdArguments,
					WindowStyle = ProcessWindowStyle.Hidden,
					ErrorDialog = false
				},
				EnableRaisingEvents = true
			};

			_logger.Debug("{0} {1}", process.StartInfo.FileName, process.StartInfo.Arguments);

			StreamReader outputReader = null;
			StreamReader errorReader = null;
			try
			{
				process.Start();
				outputReader = process.StandardOutput;
				errorReader = process.StandardError;
				_logger.Debug("Mount StdOut: " + outputReader.ReadLine());
				_logger.Debug("Mount StdErr: " +errorReader.ReadLine());
			}
			catch (Exception)
			{
				_mountSemaphore.Release();
				try
				{
					Directory.Delete(mountFolder);
				}
				catch (Exception)
				{
					throw new IOException("Unable to delete mount point " + mountFolder);
				}
				throw new IOException("Unable to mount file " + isoPath);
			}

			if (process.ExitCode == 0)
			{
				return new LinuxMount(mountFolder, isoPath, this, _logger, _umountELF, _sudoELF);
			}
			else
			{
				_mountSemaphore.Release();
				try
				{
					Directory.Delete(mountFolder);
				}
				catch (Exception)
				{
					throw new IOException("Unable to delete mount point " + mountFolder);
				}
				throw new IOException("Unable to mount file " + isoPath);
			}
		}

		public void Dispose()
		{
			Dispose(true);
		}

		protected virtual void Dispose(bool dispose)
		{
			if (dispose)
			{
				_logger.Info("Disposing LinuxMount");
			}
		}

		public bool CanMount(string path)
		{
			if ((Environment.OSVersion.Platform == PlatformID.Unix) && !(IsRunningOnMac()))
			{
				return string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase);
			}
			else
				return false;
		}

		internal void OnUnmount(LinuxMount mount)
		{
			_mountSemaphore.Release();
		}

		// From mono/mcs/class/Managed.Windows.Forms/System.Windows.Forms/XplatUI.cs
		[DllImport ("libc")]
		private static extern int uname (IntPtr buf);

		private static bool IsRunningOnMac()
		{
			IntPtr buf = IntPtr.Zero;
			try {
				buf = Marshal.AllocHGlobal (8192);
				// This is a hacktastic way of getting sysname from uname ()
				if (uname (buf) == 0) {
					string os = Marshal.PtrToStringAnsi (buf);
					if (os == "Darwin")
						return true;
				}
			} catch {
			} finally {
				if (buf != IntPtr.Zero)
					Marshal.FreeHGlobal (buf);
			}
			return false;
		}
	}
}

