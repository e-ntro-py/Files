﻿// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	internal sealed partial class OpenStorageSenseFromHomeAction : OpenStorageSenseAction
	{
		private IHomePageContext HomePageContext { get; } = Ioc.Default.GetRequiredService<IHomePageContext>();
		private readonly DrivesViewModel drivesViewModel = Ioc.Default.GetRequiredService<DrivesViewModel>();

		public override bool IsExecutable =>
			HomePageContext.IsAnyItemRightClicked &&
			HomePageContext.RightClickedItem is not null &&
			HomePageContext.RightClickedItem.Path is not null &&
			drivesViewModel.Drives
				.Cast<DriveItem>()
				.FirstOrDefault(x => string.Equals(x.Path, HomePageContext.RightClickedItem.Path)) is DriveItem driveItem &&
				driveItem.Type != DriveType.Network;

		public override bool IsAccessibleGlobally
			=> false;

		public override Task ExecuteAsync(object? parameter = null)
		{
			return StorageSenseHelper.OpenStorageSenseAsync(HomePageContext?.RightClickedItem?.Path ?? string.Empty);
		}
	}
}
