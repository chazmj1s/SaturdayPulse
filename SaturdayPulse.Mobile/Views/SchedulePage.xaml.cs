using SaturdayPulse.Models;
using SaturdayPulse.ViewModels;

namespace SaturdayPulse.Views
{
    public partial class SchedulePage : ContentPage
    {
        public SchedulePage(ScheduleViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;

            // My Teams' opponent-name navigation (2026-09-05). No unsubscribe —
            // this page (and its ScheduleViewModel) are constructed once and
            // live for the app's lifetime under MainPage's PageHost, same as
            // every other tab page in this app.
            viewModel.ScrollToGameRequested += OnScrollToGameRequested;
        }

        private void OnScrollToGameRequested(GameResult game)
        {
            GamesCollectionView.ScrollTo(game, position: ScrollToPosition.Center, animate: true);
        }
    }
}