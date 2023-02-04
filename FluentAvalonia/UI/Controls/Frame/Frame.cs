﻿using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Logging;
using Avalonia.Threading;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Text;

namespace FluentAvalonia.UI.Controls;

/// <summary>
/// Displays <see cref="UserControl"/> instances (Pages in WinUI), supports navigation to new pages, 
/// and maintains a navigation history to support forward and backward navigation.
/// </summary>
[TemplatePart(s_tpContentPresenter, typeof(ContentPresenter))]
public partial class Frame : ContentControl
{
    public Frame()
    {
        var back = new AvaloniaList<PageStackEntry>();
        var forw = new AvaloniaList<PageStackEntry>();

        back.CollectionChanged += OnBackStackChanged;
        forw.CollectionChanged += OnForwardStackChanged;

        BackStack = back;
        ForwardStack = forw;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
        {
            if (change.NewValue == null)
            {
                CurrentEntry = null;
            }
        }
        else if (change.Property == SourcePageTypeProperty)
        {
            if (!_isNavigating)
            {
                if (change.NewValue is null)
                    throw new InvalidOperationException("SourcePageType cannot be null. Use Content instead.");

                Navigate(change.GetNewValue<Type>());
            }
        }
        else if (change.Property == IsNavigationStackEnabledProperty)
        {
            if (!change.GetNewValue<bool>())
            {
                _backStack.Clear();
                _forwardStack.Clear();
                _cache.Clear();
            }
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _presenter = e.NameScope.Find<ContentPresenter>(s_tpContentPresenter);
    }

    protected override bool RegisterContentPresenter(IContentPresenter presenter)
    {
        if (presenter.Name == "ContentPresenter")
            return true;

        return base.RegisterContentPresenter(presenter);
    }

    /// <summary>
    /// Navigates to the most recent item in back navigation history, if a Frame manages its own navigation history.
    /// </summary>
    public void GoBack() => GoBack(null);

    /// <summary>
    /// Navigates to the most recent item in back navigation history, if a Frame manages its own navigation history, 
    /// and specifies the animated transition to use.
    /// </summary>
    /// <param name="infoOverride">Info about the animated transition to use.</param>
    public void GoBack(NavigationTransitionInfo infoOverride)
    {
        if (CanGoBack)
        {
            var entry = _backStack[_backStack.Count - 1];
            if (infoOverride != null)
            {
                entry.NavigationTransitionInfo = infoOverride;
            }
            else
            {
                entry.NavigationTransitionInfo = CurrentEntry?.NavigationTransitionInfo ?? null;
            }

            NavigateCore(entry, NavigationMode.Back);
        }
    }

    /// <summary>
    /// Navigates to the most recent item in forward navigation history, if a Frame manages its own navigation history.
    /// </summary>
    public void GoForward()
    {
        if (CanGoForward)
        {
            NavigateCore(_forwardStack[_forwardStack.Count - 1], NavigationMode.Forward);
        }
    }

    /// <summary>
    /// Causes the Frame to load content represented by the specified Page.
    /// </summary>
    /// <param name="sourcePageType">The page (IControl) to navigate to, specified as a type reference to its class type, or 
    /// if a <see cref="NavigationPageFactory"/> this can be any type (e.g., a ViewModel)</param>
    /// <returns><c>false</c> if a <see cref="NavigationFailed"/> event handler has set Handled to true; 
    /// otherwise, <c>true</c>.</returns>
    public bool Navigate(Type sourcePageType) => Navigate(sourcePageType, null, null);


    /// <summary>
    /// Causes the Frame to load content represented by the specified Page, also passing a parameter to be 
    /// interpreted by the target of the navigation.
    /// </summary>
    /// <param name="sourcePageType">The page (IControl) to navigate to, specified as a type reference to its class type, or 
    /// if a <see cref="NavigationPageFactory"/> this can be any type (e.g., a ViewModel)</param>
    /// <param name="parameter">The navigation parameter to pass to the target page; 
    /// must have a basic type (string, char, numeric, or GUID) to support parameter serialization
    /// using GetNavigationState.</param>
    /// <returns><c>false</c> if a <see cref="NavigationFailed"/> event handler has set Handled to true; 
    /// otherwise, <c>true</c>.</returns>
    public bool Navigate(Type sourcePageType, object parameter) => Navigate(sourcePageType, parameter, null);

    /// <summary>
    /// Causes the Frame to load content represented by the specified Page -derived data type, 
    /// also passing a parameter to be interpreted by the target of the navigation, and a value 
    /// indicating the animated transition to use.
    /// </summary>
    /// <param name="sourcePageType">The page (IControl) to navigate to, specified as a type reference to its class type, or 
    /// if a <see cref="NavigationPageFactory"/> this can be any type (e.g., a ViewModel)</param>
    /// <param name="parameter">The navigation parameter to pass to the target page; must have a 
    /// basic type (string, char, numeric, or GUID) to support parameter serialization using 
    /// GetNavigationState.</param>
    /// <param name="infoOverride">Info about the animated transition.</param>
    /// <returns><c>false</c> if a <see cref="NavigationFailed"/> event handler has set Handled to true; 
    /// otherwise, <c>true</c>.</returns>
    public bool Navigate(Type sourcePageType, object parameter, NavigationTransitionInfo infoOverride)
    {
        return NavigateCore(new PageStackEntry(sourcePageType, parameter,
            infoOverride), NavigationMode.New);
    }

    /// <summary>
    /// Causes the Frame to load content represented by the specified Page, also passing a parameter to be 
    /// interpreted by the target of the navigation.
    /// </summary>
    /// <param name="sourcePageType">The page (IControl) to navigate to, specified as a type reference to its class type, or 
    /// if a <see cref="NavigationPageFactory"/> this can be any type (e.g., a ViewModel)</param>
    /// <param name="parameter">The navigation parameter to pass to the target page; must have a basic type 
    /// (string, char, numeric, or GUID) to support parameter serialization using GetNavigationState.</param>
    /// <param name="navOptions">Options for the navigation, including whether it is recorded in the navigation stack 
    /// and what transition animation is used.</param>
    /// <returns><c>false</c> if a <see cref="NavigationFailed"/> event handler has set Handled to true; 
    /// otherwise, <c>true</c>.</returns>
    public bool NavigateToType(Type sourcePageType, object parameter, FrameNavigationOptions navOptions) =>
        NavigateCore(new PageStackEntry(sourcePageType, parameter, navOptions?.TransitionInfoOverride),
            NavigationMode.New, navOptions);

    /// <summary>
    /// Causes the frame to load content represented by the specified target property with the
    /// specified navigation options
    /// </summary>
    /// <remarks>
    /// You must specify a <see cref="NavigationPageFactory"/> for this method to succeed
    /// </remarks>
    /// <param name="target">An existing object for which page creation should be based (e.g., A ViewModel instance)</param>
    /// <param name="navOptions">Options for the navigation, including whether it is recorded in the navigation stack 
    /// and what transition animation is used.</param>
    /// <returns><c>false</c> if a <see cref="NavigationFailed"/> event handler has set Handled to true or
    /// if <see cref="NavigationPageFactory" /> is not specified; otherwise, <c>true</c>.</returns>
    public bool NavigateFromObject(object target, FrameNavigationOptions navOptions = null)
    {
        // Check the cache first to see if we have an existing page that matches
        // For this check we check by both type and object reference
        var existing = CheckCacheAndGetPage(target.GetType(), target);

        if (existing == null)
        {
            // If we don't have a previous reference, try to resolve via Factory
            existing = NavigationPageFactory.GetPageFromObject(target);

            // Unable to locate page, return false
            if (existing == null)
                return false;
        }

        // The page source Type here will be whatever was specified as 'target'
        var entry = new PageStackEntry(target.GetType(), null, navOptions?.TransitionInfoOverride)
        {
            Instance = existing
        };

        return NavigateCore(entry, NavigationMode.New, navOptions);
    }

    /// <summary>
    /// Serializes the Frame navigation history into a string
    /// </summary>
    /// <returns></returns>
    public string GetNavigationState()
    {
        if (!IsNavigationStackEnabled)
            throw new InvalidOperationException("Cannot retreive navigation stack when IsNavigationStackEnabled is false");

        // Format of the Navigation state string - this is not the same as WinUI
        // Full.Type.Name.Here|Serialized Parameter // First line is the current page
        // N // Number of pages in BackStack
        // -- BackStack Entries here, same format as above
        // N // Number of pages in ForwardStack
        // -- ForwardStack Entries here, same format as above

        static void AppendEntry(StringBuilder sb, PageStackEntry entry)
        {
            sb.Append(entry.SourcePageType.AssemblyQualifiedName);
            sb.Append('|');
            if (entry.Parameter != null)
            {
                sb.Append(entry.Parameter.ToString());
            }
            sb.AppendLine();
        }

        var sb = new StringBuilder();

        if (CurrentEntry != null)
        {
            AppendEntry(sb, CurrentEntry);
        }

        sb.AppendLine(BackStackDepth.ToString());

        for (int i = 0; i < BackStackDepth; i++)
        {
            AppendEntry(sb, BackStack[i]);
        }

        sb.AppendLine(ForwardStack.Count.ToString());

        for (int i = 0; i < ForwardStack.Count; i++)
        {
            AppendEntry(sb, ForwardStack[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Reads and restores the navigation history of a Frame from a provided serialization string.
    /// </summary>
    /// <param name="navState">The serialization string that supplies the restore point for navigation history.</param>
    public void SetNavigationState(string navState) =>
        SetNavigationState(navState, false);

    /// <summary>
    /// Reads and restores the navigation history of a Frame from a provided serialization string,
    /// and optionally supresses navigation to the last page type
    /// </summary>
    /// <param name="navState">The serialization string that supplies the restore point for navigation history.</param>
    /// <param name="suppressNavigate">true to restore navigation history without navigating to the current page; otherwise, false.</param>
    /// <remarks>
    /// Calling SetNavigationState with suppressNavigate set to true, OnNavigatedTo is not called and the current page is placed into
    /// the BackStack
    /// </remarks>
    public void SetNavigationState(string navState, bool suppressNavigate)
    {
        if (!IsNavigationStackEnabled)
            throw new InvalidOperationException("Cannot set navigation stack when IsNavigationStackEnabled is false");

        BackStack.Clear();
        ForwardStack.Clear();
        CurrentEntry = null;
        Content = null;
        _cache.Clear();

        // Format of the Navigation state string - this is not the same as WinUI
        // Full.Type.Name.Here|Serialized Parameter // First line is the current page
        // N // Number of pages in BackStack
        // -- BackStack Entries here, same format as above
        // N // Number of pages in ForwardStack
        // -- ForwardStack Entries here, same format as above

        using (var reader = new StringReader(navState))
        {
            var firstLine = reader.ReadLine(); // Current Page

            bool addCurrentEntryToBackStack = false;
            // Current page was null when saved, don't restore null
            // This is the only place we're allowed to have null - since a call to
            // Navigate(null) will fail to navigate & nothing is added to the stack
            if (firstLine[0] != '|')
            {
                var indexOfSep = firstLine.IndexOf('|');
                var pageType = Type.GetType(firstLine.Substring(0, indexOfSep));
                var param = firstLine.Substring(indexOfSep + 1);
                CurrentEntry = new PageStackEntry(pageType, param, null);

                if (!suppressNavigate)
                {
                    var page = CreatePageAndCacheIfNecessary(pageType);
                    CurrentEntry.Instance = page;

                    SetContentAndAnimate(CurrentEntry);
                    // We only raise the NavigatedEvent 
                    page.RaiseEvent(new NavigationEventArgs(page, NavigationMode.New, null, param, pageType) { RoutedEvent = NavigatedToEvent });
                }
                else
                {
                    addCurrentEntryToBackStack = true;
                }
            }

            var numBackLine = int.Parse(reader.ReadLine());

            for (int i = 0; i < numBackLine; i++)
            {
                var line = reader.ReadLine();
                var indexOfSep = line.IndexOf('|');
                var pageType = Type.GetType(line.Substring(0, indexOfSep));

                if (pageType == null)
                {
                    // Don't fail if we get an invalid page, log & continue
                    Logger.TryGet(LogEventLevel.Error, "Frame")?
                        .Log("Frame", $"Attempting to parse the type '{line.Substring(0, indexOfSep)}' failed. Page was skipped");

                    continue;
                }

                var param = line.Substring(indexOfSep + 1);

                var entry = new PageStackEntry(pageType, param, null);
                BackStack.Add(entry);
            }

            if (addCurrentEntryToBackStack)
            {
                BackStack.Add(CurrentEntry);
                CurrentEntry = null;
            }

            var numForwardLine = int.Parse(reader.ReadLine());

            for (int i = 0; i < numForwardLine; i++)
            {
                var line = reader.ReadLine();
                var indexOfSep = line.IndexOf('|');
                var pageType = Type.GetType(line.Substring(0, indexOfSep));
                var param = line.Substring(indexOfSep + 1);

                if (pageType == null)
                {
                    // Don't fail if we get an invalid page, log & continue
                    Logger.TryGet(LogEventLevel.Error, "Frame")?
                        .Log("Frame", $"Attempting to parse the type '{line.Substring(0, indexOfSep)}' failed. Page was skipped");

                    continue;
                }

                var entry = new PageStackEntry(pageType, param, null);
                ForwardStack.Add(entry);
            }
        }
    }

    private bool NavigateCore(PageStackEntry entry, NavigationMode mode, FrameNavigationOptions options = null)
    {
        try
        {
            _isNavigating = true;

            var ea = new NavigatingCancelEventArgs(mode,
                entry.NavigationTransitionInfo,
                entry.Parameter,
                entry.SourcePageType);

            Navigating?.Invoke(this, ea);

            if (ea.Cancel)
            {
                OnNavigationStopped(entry, mode);
                return false;
            }

            // Tell the current page we want to navigate away from it
            if (CurrentEntry?.Instance is Control oldPage)
            {
                ea.RoutedEvent = NavigatingFromEvent;
                oldPage.RaiseEvent(ea);

                if (ea.Cancel)
                {
                    OnNavigationStopped(entry, mode);
                    return false;
                }
            }

            // Navigate to new page
            var prevEntry = CurrentEntry;
            bool wasPageSet = entry.Instance != null;

            if (mode == NavigationMode.New && !wasPageSet)
            {
                // Check if we already have an instance of the page in the cache
                entry.Instance = CheckCacheAndGetPage(entry.SourcePageType);
            }

            if (entry.Instance == null)
            {
                var page = CreatePageAndCacheIfNecessary(entry.SourcePageType);
                if (page == null)
                {
                    throw new ArgumentException($"The type {entry.SourcePageType} is not a valid page type.");
                }

                entry.Instance = page;
            }
            else if (wasPageSet)
            {
                // The page was already create for us when passed in (NavigateFromObject path)
                // Try adding to the cache now
                TryAddToCache(entry.SourcePageType, entry.Instance);
            }

            var oldEntry = CurrentEntry;
            CurrentEntry = entry;

            var navEA = new NavigationEventArgs(
                CurrentEntry.Instance,
                mode, entry.NavigationTransitionInfo,
                entry.Parameter,
                entry.SourcePageType);

            // Old page is now unloaded, raise OnNavigatedFrom
            if (oldEntry != null)
            {
                navEA.RoutedEvent = NavigatedFromEvent;
                oldEntry.Instance.RaiseEvent(navEA);
            }

            SetContentAndAnimate(entry);

            bool addToNavStack = options?.IsNavigationStackEnabled ?? IsNavigationStackEnabled;

            if (addToNavStack)
            {
                switch (mode)
                {
                    case NavigationMode.New:
                        ForwardStack.Clear();
                        if (prevEntry != null)
                        {
                            if (BackStack.Count == CacheSize)
                            {
                                if (BackStack.Count > 0)
                                {
                                    BackStack.RemoveAt(0);
                                }
                            }

                            BackStack.Add(prevEntry);
                        }
                        break;

                    case NavigationMode.Back:
                        ForwardStack.Add(prevEntry);
                        BackStack.Remove(CurrentEntry);
                        break;

                    case NavigationMode.Forward:
                        BackStack.Add(prevEntry);
                        ForwardStack.Remove(CurrentEntry);
                        break;

                    case NavigationMode.Refresh:
                        break;
                }
            }


            SourcePageType = entry.SourcePageType;
            //CurrentSourcePageType = entry.SourcePageType;

            Navigated?.Invoke(this, navEA);

            // New Page is loaded, let's tell the page
            if (entry.Instance is Control newPage)
            {
                navEA.RoutedEvent = NavigatedToEvent;
                newPage.RaiseEvent(navEA);
            }

            //Need to find compatible method for this
            //VisualTreeHelper.CloseAllPopups();

            return true;
        }
        catch (Exception ex)
        {
            NavigationFailed?.Invoke(this, new NavigationFailedEventArgs(ex, entry.SourcePageType));

            //I don't really want to throw an exception and break things. Just return false
            return false;
        }
        finally
        {
            _isNavigating = false;
        }
    }

    private void OnNavigationStopped(PageStackEntry entry, NavigationMode mode)
    {
        NavigationStopped?.Invoke(this, new NavigationEventArgs(entry.Instance,
            mode, entry.NavigationTransitionInfo, entry.Parameter, entry.SourcePageType));
    }

    private void OnForwardStackChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // 11.0 changed the API surface an outside implementations can no longer call RaisePropertyChanged and will need to 
        // "Set" the property to do so. CanGoBack and CanGoForward derive their value by checking the list counts and don't
        // use a backing boolean field so prior to 11.0 I just used RaisePropertyChanged. Now, I've made the CLR properties
        // private set, and we use SetAndRaise but just throw away the ref param
        CanGoForward = _forwardStack.Count > 0;
    }

    private void OnBackStackChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        // 11.0 changed the API surface an outside implementations can no longer call RaisePropertyChanged and will need to 
        // "Set" the property to do so. CanGoBack and CanGoForward derive their value by checking the list counts and don't
        // use a backing boolean field so prior to 11.0 I just used RaisePropertyChanged. Now, I've made the CLR properties
        // private set, and we use SetAndRaise but just throw away the ref param
        CanGoForward = _forwardStack.Count > 0;
        CanGoBack = _backStack.Count > 0;
    }

    private Control CreatePageAndCacheIfNecessary(Type srcPageType)
    {
        if (CacheSize == 0)
        {
            return NavigationPageFactory?.GetPage(srcPageType) ??
                Activator.CreateInstance(srcPageType) as Control;
        }

        for (int i = 0; i < _cache.Count; i++)
        {
            if (_cache[i].pageSrcType == srcPageType)
            {
                throw new Exception($"An object of type {srcPageType} has already been added to the Navigation Stack");
            }
        }

        var newPage = NavigationPageFactory?.GetPage(srcPageType) ??
            Activator.CreateInstance(srcPageType) as Control;

        _cache.Add((srcPageType, newPage));
        if (_cache.Count > CacheSize)
        {
            _cache.RemoveAt(0);
        }

        return newPage;
    }

    private Control CheckCacheAndGetPage(Type srcPageType = null, object target = null)
    {
        if (CacheSize == 0)
            return null;

        for (int i = _cache.Count - 1; i >= 0; i--)
        {
            if (_cache[i].pageSrcType == srcPageType || _cache[i].page == target)
            {
                return _cache[i].page;
            }
        }

        return null;
    }

    private void TryAddToCache(Type srcType, Control page)
    {
        for (int i = _cache.Count - 1; i >= 0; i--)
        {
            // Already exists in the cache, exit
            if (_cache[i].pageSrcType == srcType || page == _cache[i].page)
                return;
        }

        _cache.Add((srcType, page));
    }

    private void SetContentAndAnimate(PageStackEntry entry)
    {
        if (entry == null)
            return;

        Content = entry.Instance;

        if (_presenter != null)
        {
            //Default to entrance transition
            entry.NavigationTransitionInfo = entry.NavigationTransitionInfo ?? new EntranceNavigationTransitionInfo();
            _presenter.Opacity = 0;
            // Very busy pages will delay loading b/c layout & render has to occur first
            // Posting this helps a little bit, but not much
            // Not really sure how to get the transition to occur while the page is loading
            // so speed is comparable to WinUI...this may be an Avalonia limitation???
            Dispatcher.UIThread.Post(() =>
            {
                entry.NavigationTransitionInfo.RunAnimation(_presenter);
            }, DispatcherPriority.Loaded);
        }
    }

    private ContentPresenter _presenter;
    private readonly List<(Type pageSrcType, Control page)> _cache = new List<(Type, Control)>(10);
    private bool _isNavigating = false;

    private const string s_tpContentPresenter = "ContentPresenter";
}
