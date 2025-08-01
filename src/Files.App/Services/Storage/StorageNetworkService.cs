// Copyright (c) Files Community
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using Vanara.PInvoke;
using Vanara.Windows.Shell;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.NetworkManagement.WNet;
using Windows.Win32.Security.Credentials;

namespace Files.App.Services
{
	public sealed partial class NetworkService : ObservableObject, INetworkService
	{
		private ICommonDialogService CommonDialogService { get; } = Ioc.Default.GetRequiredService<ICommonDialogService>();

		private readonly static string guid = "::{f02c1a0d-be21-4350-88b0-7367fc96ef3c}";


		private ObservableCollection<IFolder> _Computers = [];
		/// <inheritdoc/>
		public ObservableCollection<IFolder> Computers
		{
			get => _Computers;
			private set => SetProperty(ref _Computers, value);
		}

		private ObservableCollection<IFolder> _Shortcuts = [];
		/// <inheritdoc/>
		public ObservableCollection<IFolder> Shortcuts
		{
			get => _Shortcuts;
			private set => SetProperty(ref _Shortcuts, value);
		}

		/// <summary>
		/// Initializes an instance of <see cref="NetworkService"/>.
		/// </summary>
		public NetworkService()
		{
			var networkItem = new DriveItem()
			{
				DeviceID = "network-folder",
				Text = Strings.Network.GetLocalizedResource(),
				Path = Constants.UserEnvironmentPaths.NetworkFolderPath,
				Type = DriveType.Network,
				ItemType = NavigationControlItemType.Drive,
			};

			networkItem.MenuOptions = new ContextMenuOptions()
			{
				IsLocationItem = true,
				ShowEjectDevice = networkItem.IsRemovable,
				ShowShellItems = true,
				ShowProperties = true,
			};
			lock (_Computers)
				_Computers.Add(networkItem);
		}

		/// <inheritdoc/>
		public async Task<IEnumerable<IFolder>> GetComputersAsync()
		{
			var result = await Win32Helper.GetShellFolderAsync(guid, false, true, 0, int.MaxValue);

			return result.Enumerate.Where(item => item.IsFolder).Select(item =>
			{
				var networkItem = new DriveItem()
				{
					Text = item.FileName,
					Path = item.FilePath,
					DeviceID = item.FilePath,
					Type = DriveType.Network,
					ItemType = NavigationControlItemType.Drive,
				};

				networkItem.MenuOptions = new ContextMenuOptions()
				{
					IsLocationItem = true,
					ShowEjectDevice = networkItem.IsRemovable,
					ShowShellItems = true,
					ShowProperties = true,
				};

				return networkItem;
			});
		}

		/// <inheritdoc/>
		public async Task<IEnumerable<IFolder>> GetShortcutsAsync()
		{
			var networkLocations = await STATask.Run(() =>
			{
				var locations = new List<ShellLinkItem>();
				using (var netHood = new ShellFolder(Shell32.KNOWNFOLDERID.FOLDERID_NetHood))
				{
					foreach (var item in netHood)
					{
						if (item is ShellLink link)
						{
							locations.Add(ShellFolderExtensions.GetShellLinkItem(link));
						}
						else
						{
							var linkPath = (string?)item?.Properties["System.Link.TargetParsingPath"];
							if (linkPath is not null)
							{
								var linkItem = ShellFolderExtensions.GetShellFileItem(item);
								locations.Add(new(linkItem) { TargetPath = linkPath });
							}
						}
					}
				}
				return locations;
			}, App.Logger);

			return (networkLocations ?? Enumerable.Empty<ShellLinkItem>()).Select(item =>
			{
				var networkItem = new DriveItem()
				{
					Text = item.FileName,
					Path = item.TargetPath,
					DeviceID = item.FilePath,
					Type = DriveType.Network,
					ItemType = NavigationControlItemType.Drive,
				};

				networkItem.MenuOptions = new ContextMenuOptions()
				{
					IsLocationItem = true,
					ShowEjectDevice = networkItem.IsRemovable,
					ShowShellItems = true,
					ShowProperties = true,
				};
				return networkItem;
			});
		}

		/// <inheritdoc/>
		public async Task UpdateComputersAsync()
		{
			var unsortedDrives = new List<IFolder>()
			{
				_Computers.Single(x => x is DriveItem o && o.DeviceID == "network-folder")
			};

			foreach (var item in await GetComputersAsync())
				unsortedDrives.Add(item);

			var orderedDrives =
				unsortedDrives.Cast<DriveItem>()
					.OrderByDescending(o => o.DeviceID == "network-folder")
					.ThenBy(o => o.Text);

			Computers.Clear();

			foreach (var item in orderedDrives)
				Computers.AddIfNotPresent(item);
		}

		/// <inheritdoc/>
		public async Task UpdateShortcutsAsync()
		{
			var unsortedDrives = new List<IFolder>();

			foreach (var item in await GetShortcutsAsync())
				unsortedDrives.Add(item);

			var orderedDrives =
				unsortedDrives.Cast<DriveItem>()
					.OrderBy(o => o.Text);

			Shortcuts.Clear();

			foreach (var item in orderedDrives)
				Shortcuts.AddIfNotPresent(item);
		}

		/// <inheritdoc/>
		public bool DisconnectNetworkDrive(IFolder drive)
		{
			return
				PInvoke.WNetCancelConnection2W(
					drive.Id.TrimEnd('\\'),
					NET_CONNECT_FLAGS.CONNECT_UPDATE_PROFILE,
					true)
				is WIN32_ERROR.NO_ERROR;
		}

		/// <inheritdoc/>
		public Task OpenMapNetworkDriveDialogAsync()
		{
			return STATask.Run(() =>
			{
				return CommonDialogService.Open_NetworkConnectionDialog(
					MainWindow.Instance.WindowHandle,
					useMostRecentPath: true,
					hideRestoreConnectionCheckBox: false);
			}, App.Logger);
		}

		/// <inheritdoc/>
		public async Task<bool> AuthenticateNetworkShare(string path)
		{
			var netRes = new NETRESOURCEW() { dwType = NET_RESOURCE_TYPE.RESOURCETYPE_DISK };

			unsafe
			{

				if (!path.StartsWith(@"\\", StringComparison.Ordinal))
				{
					//  Special handling for network drives
					//  This part will change path from "y:\Download" to "\\192.168.0.1\nfs\Download"
					[DllImport("mpr.dll", CharSet = CharSet.Auto)]
					static extern int WNetGetConnection(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

					StringBuilder remoteName = new StringBuilder(300);
					int length = remoteName.Capacity;
					string lpLocalName = path.Substring(0, 2);

					int ret = WNetGetConnection(lpLocalName, remoteName, ref length);

					if (ret == 0)
						path = path.Replace(lpLocalName, remoteName.ToString());

				}

				fixed (char* lpcPath = path)
					netRes.lpRemoteName = new PWSTR(lpcPath);
			}

			// If credentials are saved, this will return NO_ERROR
			var res = (WIN32_ERROR)PInvoke.WNetAddConnection3W(new(nint.Zero), netRes, null, null, 0);

			if (res == WIN32_ERROR.ERROR_LOGON_FAILURE || res == WIN32_ERROR.ERROR_ACCESS_DENIED)
			{
				var dialog = DynamicDialogFactory.GetFor_CredentialEntryDialog(path);
				await dialog.ShowAsync();
				var credentialsReturned = dialog.ViewModel.AdditionalData as string[];

				if (credentialsReturned is not null && credentialsReturned[1] != null)
				{
					res = (WIN32_ERROR)PInvoke.WNetAddConnection3W(new(nint.Zero), netRes, credentialsReturned[1], credentialsReturned[0], 0);
					if (credentialsReturned[2] == "y" && res == WIN32_ERROR.NO_ERROR)
					{
						var creds = new CREDENTIALW()
						{
							Type = CRED_TYPE.CRED_TYPE_DOMAIN_PASSWORD,
							AttributeCount = 0,
							Persist = CRED_PERSIST.CRED_PERSIST_ENTERPRISE
						};

						unsafe
						{
							fixed (char* lpcTargetName = path.Substring(2))
								creds.TargetName = new(lpcTargetName);

							fixed (char* lpcUserName = credentialsReturned[0])
								creds.UserName = new(lpcUserName);

							byte[] bPassword = Encoding.Unicode.GetBytes(credentialsReturned[1]);
							fixed (byte* lpCredentialBlob = bPassword)
								creds.CredentialBlob = lpCredentialBlob;

							creds.CredentialBlobSize = (uint)bPassword.Length;
						}

						PInvoke.CredWrite(creds, 0);
					}
				}
				else
				{
					return false;
				}
			}

			if (res == WIN32_ERROR.NO_ERROR)
			{
				return true;
			}
			else
			{
				await DialogDisplayHelper.ShowDialogAsync(Strings.NetworkFolderErrorDialogTitle.GetLocalizedResource(), res.ToString());

				return false;
			}
		}
	}
}
