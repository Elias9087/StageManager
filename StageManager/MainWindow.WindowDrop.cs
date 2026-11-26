using StageManager.Core;
using StageManager.Model;
using StageManager.Native;
using StageManager.Native.PInvoke;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace StageManager
{
	/// <summary>
	/// The sidebar while a window of the stage is dragged at its title bar: it comes in when the pointer is above it and marks the target,
	/// a card or the place of a new card. The drop itself is done by <see cref="SceneManager"/>, which asks for the target at the end.
	/// Everything runs on the UI thread in the timer of the presence, no mouse hook: Windows holds the mouse in its move loop.
	/// </summary>
	public partial class MainWindow
	{
		/// <summary>Seconds the button may be up without the end of the move before the drag counts as lost.</summary>
		private const double LostWindowDragSeconds = 1.0;

		/// <summary>Space between the placeholder of a new card and the edge of the sidebar, in DIP.</summary>
		private const double PlaceholderMargin = 8;

		private readonly WindowDropTargets _windowDrop = new();
		private bool _windowDropActive;
		private SceneModel? _windowDropCard;
		private double? _windowDropButtonUpSince;

		/// <summary>A window is dragged and may still be dropped: the sidebar comes in above its whole width.</summary>
		private bool IsWindowDropPossible => SceneManager?.WindowDrag is not null && !_windowDrop.IsCancelled;

		/// <summary>Called by the timer of the presence, before the presence decides.</summary>
		private void UpdateWindowDrop()
		{
			var drag = SceneManager?.WindowDrag;
			if (drag is null)
			{
				if (_windowDropActive)
					EndWindowDrop();
				return;
			}

			if (!_windowDropActive)
			{
				_windowDropActive = true;
				_windowDrop.Reset();
				_windowDropButtonUpSince = null;
			}

			// the end of the move never came: the window is gone, or the event got lost
			if (!Win32.IsWindow(drag.Window.Handle))
			{
				SceneManager!.AbortWindowDrag("the window is gone");
				EndWindowDrop();
				return;
			}

			if (WindowDragProbe.IsPrimaryButtonDown())
			{
				_windowDropButtonUpSince = null;
			}
			else
			{
				var now = _clock.Elapsed.TotalSeconds;
				_windowDropButtonUpSince ??= now;
				if (now - _windowDropButtonUpSince > LostWindowDragSeconds)
				{
					SceneManager!.AbortWindowDrag("the button is up, but the move did not end");
					EndWindowDrop();
					return;
				}
			}

			if (_windowDrop.IsCancelled)
				return;

			// Windows puts the window back on Escape, nothing is a target any more until the drag ends
			if (HeldKeys.IsEscapeDown())
			{
				DebugLog.Write("DROP", "escape: no target until the drag ends");
				if (_windowDrop.Cancel())
					ShowWindowDropTarget(WindowDropTarget.None);
				return;
			}

			TrackWindowDropTarget(drag);
		}

		private void TrackWindowDropTarget(WindowDragSession drag)
		{
			if (!Win32.GetCursorPos(out var cursor))
				return;

			var cards = new List<(Guid, Area)>();
			foreach (var model in Scenes)
			{
				if (model.IsVisible && CardBounds(model) is Area area)
					cards.Add((model.Id, area));
			}

			var shown = IsVisible && Mode != SidebarMode.OffScreen;
			if (!_windowDrop.Update(cursor.X, cursor.Y, _geometry, cards, shown, drag.CanMakeOwnCard, WindowDropTargets.EdgeGuard * _dpiScale))
				return;

			DebugLog.Write("DROP", $"target {SceneManager!.DescribeDropTarget(_windowDrop.Target)} at ({cursor.X},{cursor.Y})");
			ShowWindowDropTarget(_windowDrop.Target);
		}

		/// <summary>The move ended: the target under the pointer now, and whether Escape was pressed. The marks go away.</summary>
		private (WindowDropTarget Target, bool EscapeSeen) FinishWindowDrop()
		{
			if (SceneManager?.WindowDrag is WindowDragSession drag && !_windowDrop.IsCancelled)
			{
				if (HeldKeys.IsEscapeDown())
					_windowDrop.Cancel();
				else
					TrackWindowDropTarget(drag);
			}

			var result = (_windowDrop.Target, _windowDrop.IsCancelled);
			EndWindowDrop();
			return result;
		}

		private void EndWindowDrop()
		{
			_windowDropActive = false;
			_windowDrop.Reset();
			_windowDropButtonUpSince = null;
			ShowWindowDropTarget(WindowDropTarget.None);
		}

		private void ShowWindowDropTarget(WindowDropTarget target)
		{
			var card = target.Kind == WindowDropKind.Card ? Scenes.FirstOrDefault(m => m.Id == target.Card) : null;

			if (!ReferenceEquals(card, _windowDropCard))
			{
				if (_windowDropCard is object)
					_windowDropCard.IsMergeTarget = false;

				_windowDropCard = card;

				if (card is object)
					card.IsMergeTarget = true;
			}

			if (target.Kind == WindowDropKind.NewCard)
				ShowNewCardPlaceholder();
			else
				newCardPlaceholder.Visibility = Visibility.Collapsed;
		}

		/// <summary>
		/// The placeholder sits below the last card, where the new card will appear. It lies above the stack and moves no card, otherwise
		/// the cards would move under the pointer and the target would jump.
		/// </summary>
		private void ShowNewCardPlaceholder()
		{
			var width = _layout.ThumbnailWidth;
			var height = _layout.ThumbnailHeight;
			newCardPlaceholder.Width = width;
			newCardPlaceholder.Height = height;

			var right = App.Settings.Current.Sidebar.Side == SidebarSide.Right;
			var left = right ? ActualWidth - SidebarLayout.EdgePadding - width : SidebarLayout.EdgePadding;

			var cards = SidebarCards();
			var top = (ActualHeight - height) / 2;

			if (cards.Count > 0
				&& scenesControl.ItemContainerGenerator.ContainerFromItem(cards[^1]) is FrameworkElement last && last.ActualHeight > 0
				&& scenesControl.ItemContainerGenerator.ContainerFromItem(cards[0]) is FrameworkElement first && first.ActualHeight > 0)
			{
				// the container of a card holds the space below it already
				var below = last.TransformToAncestor(this).Transform(new Point(0, last.ActualHeight)).Y;
				var above = first.TransformToAncestor(this).Transform(new Point(0, 0)).Y - height - PlaceholderMargin;

				if (below + height + PlaceholderMargin <= ActualHeight)
					top = below;
				else if (above >= PlaceholderMargin)
					top = above;
				else
					top = ActualHeight - height - PlaceholderMargin;
			}

			System.Windows.Controls.Canvas.SetLeft(newCardPlaceholder, Math.Max(0, left));
			System.Windows.Controls.Canvas.SetTop(newCardPlaceholder, Math.Max(0, top));
			newCardPlaceholder.Visibility = Visibility.Visible;
		}
	}
}
