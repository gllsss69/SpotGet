// SpotGet Frontend

const form = document.getElementById('trackForm');
const urlInput = document.getElementById('spotifyUrl');
const pasteBtn = document.getElementById('pasteBtn');
const submitBtn = document.getElementById('submitBtn');
const btnText = submitBtn.querySelector('.btn-text');
const btnLoader = submitBtn.querySelector('.btn-loader');

const errorSection = document.getElementById('errorSection');
const downloadNotification = document.getElementById('downloadNotification');
const downloadNotificationMessage = document.getElementById('downloadNotificationMessage');
let downloadNotificationTimeout;
let downloadNotificationHideTimeout;
const errorMessage = document.getElementById('errorMessage');

const resultSection = document.getElementById('resultSection');
const collectionSection = document.getElementById('collectionSection');
const collectionTitle = document.getElementById('collectionTitle');
const collectionType = document.getElementById('collectionType');
const collectionCoverWrap = document.getElementById('collectionCoverWrap');
const collectionCover = document.getElementById('collectionCover');
const downloadCollectionCoverBtn = document.getElementById('downloadCollectionCoverBtn');
const collectionTrackCount = document.getElementById('collectionTrackCount');
const collectionTrackList = document.getElementById('collectionTrackList');
const collectionSpotifyLink = document.getElementById('collectionSpotifyLink');
const downloadCollectionBtn = document.getElementById('downloadCollectionBtn');
const collectionDlText = downloadCollectionBtn.querySelector('.collection-dl-text');
const collectionDlLoader = downloadCollectionBtn.querySelector('.collection-dl-loader');
const collectionDlProgress = downloadCollectionBtn.querySelector('.collection-dl-progress');
const coverImg = document.getElementById('coverImg');
const trackTitle = document.getElementById('trackTitle');
const trackArtist = document.getElementById('trackArtist');
const trackAlbum = document.getElementById('trackAlbum');
const trackDuration = document.getElementById('trackDuration');
const trackLink = document.getElementById('trackLink');

const downloadBtn = document.getElementById('downloadBtn');
const dlText = downloadBtn.querySelector('.dl-text');
const dlLoader = downloadBtn.querySelector('.dl-loader');

const resultCover = document.querySelector('.result-cover');
const downloadCoverBtn = document.getElementById('downloadCoverBtn');
const coverOverlay = document.querySelector('.cover-overlay');
const previewProgress = document.getElementById('previewProgress');

const artistSection     = document.getElementById('artistSection');
const artistAvatarWrap  = document.querySelector('.artist-avatar-wrap');
const artistAvatar      = document.getElementById('artistAvatar');
const artistName        = document.getElementById('artistName');
const artistDescription = document.getElementById('artistDescription');
const artistLink        = document.getElementById('artistLink');
const artistLinkLabel   = document.getElementById('artistLinkLabel');

let currentAudio = null;
let isPlaying = false;
let currentCollection = null;

// Paste button
pasteBtn.addEventListener('click', async () => {
    try {
        const text = await navigator.clipboard.readText();
        urlInput.value = text;
        urlInput.focus();
    } catch {
        // Clipboard API may be blocked — ignore silently
    }
});

// Form submit
form.addEventListener('submit', async (e) => {
    e.preventDefault();

    const url = urlInput.value.trim();
    if (!url) return;

    setLoading(true);
    hideError();
    hideResult();
    stopAudio();

    try {
        const collectionMatch = url.match(/spotify\.com\/(?:embed\/)?(album|playlist)\/[a-zA-Z0-9]{22}/i);
        const endpoint = collectionMatch ? '/api/collection-info' : '/api/track-info';
        const response = await fetch(endpoint, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url }),
        });

        const data = await response.json();

        if (!response.ok) {
            showError(translateError(data.error || data.detail || 'Unknown error'));
            return;
        }

        if (collectionMatch) showCollection(data);
        else showResult(data);
    } catch (err) {
        showError(translations[currentLang].errConn);
    } finally {
        setLoading(false);
    }
});

downloadCollectionBtn.addEventListener('click', async () => {
    const url = currentCollection?.spotifyUrl || urlInput.value.trim();
    if (!url) return;

    setCollectionDownloadLoading(true);
    hideError();
    try {
        const startResponse = await fetch('/api/download-collection', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify({ url }),
        });
        if (!startResponse.ok) {
            const data = await startResponse.json().catch(() => ({}));
            showError(translateError(data.error || data.detail || translations[currentLang].errDl));
            return;
        }

        const { jobId } = await startResponse.json();
        if (!jobId) throw new Error('Missing collection download job id.');

        let jobStatus;
        while (true) {
            await new Promise(resolve => window.setTimeout(resolve, 4000));
            const statusResponse = await fetch(`/api/download-collection/${encodeURIComponent(jobId)}`, {
                cache: 'no-store',
            });
            if (statusResponse.status === 429) continue;
            if (!statusResponse.ok) {
                const data = await statusResponse.json().catch(() => ({}));
                showError(translateError(data.error || data.detail || translations[currentLang].errDl));
                return;
            }

            jobStatus = await statusResponse.json();
            if (jobStatus.status === 'preparing') {
                collectionDlProgress.textContent = translations[currentLang].collectionPreparing;
            } else if (jobStatus.status === 'downloading') {
                collectionDlProgress.textContent = translations[currentLang].collectionProgress
                    .replace('{progress}', jobStatus.progress)
                    .replace('{total}', jobStatus.total);
            } else if (jobStatus.status === 'packing') {
                collectionDlProgress.textContent = translations[currentLang].collectionPacking;
            } else if (jobStatus.status === 'sending') {
                collectionDlProgress.textContent = translations[currentLang].collectionSending;
            }

            if (jobStatus.status === 'failed') {
                showError(translateError(jobStatus.error || translations[currentLang].errDl));
                return;
            }
            if (jobStatus.status === 'completed') break;
        }

        const filename = jobStatus.fileName || `${collectionTitle.textContent || 'Spotify collection'}.zip`;
        const anchor = document.createElement('a');
        anchor.href = `/api/download-collection/${encodeURIComponent(jobId)}/file`;
        anchor.download = filename;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();

        while (true) {
            await new Promise(resolve => window.setTimeout(resolve, 5000));
            const statusResponse = await fetch(`/api/download-collection/${encodeURIComponent(jobId)}`, {
                cache: 'no-store',
            });
            if (statusResponse.status === 429) continue;
            if (!statusResponse.ok) break;
            jobStatus = await statusResponse.json();
            if (jobStatus.status === 'sending' || jobStatus.status === 'completed') {
                collectionDlProgress.textContent = translations[currentLang].collectionSending;
                continue;
            }
            if (jobStatus.status === 'failed') {
                showError(translateError(jobStatus.error || translations[currentLang].errDl));
                return;
            }
            break;
        }

        showDownloadNotification(filename);

        const skippedTracks = Number(jobStatus.skipped || 0);
        if (skippedTracks > 0) {
            showWarning(translations[currentLang].partialDownload.replace('{count}', skippedTracks));
        }
    } catch {
        showError(translations[currentLang].errDlGeneric);
    } finally {
        setCollectionDownloadLoading(false);
    }
});

// Download button
downloadBtn.addEventListener('click', async () => {
    const url = urlInput.value.trim();
    if (!url) return;

    setDownloadLoading(true);
    hideError();

    try {
        const response = await fetch('/api/download', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url }),
        });

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            showError(translateError(data.error || data.detail || translations[currentLang].errDl));
            return;
        }

        const blob = await response.blob();

        let filename = `${trackArtist.textContent} - ${trackTitle.textContent}.mp3`;
        const disposition = response.headers.get('Content-Disposition');
        if (disposition) {
            const utf8Match = disposition.match(/filename\*=UTF-8''([^;\n]*)/i);
            if (utf8Match && utf8Match[1]) {
                filename = decodeURIComponent(utf8Match[1]);
            } else {
                const match = disposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
                if (match && match[1]) {
                    filename = decodeURIComponent(match[1].replace(/['"]/g, ''));
                }
            }
        }

        const downloadUrl = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = downloadUrl;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        window.URL.revokeObjectURL(downloadUrl);
        a.remove();
        showDownloadNotification(filename);

    } catch (err) {
        showError(translations[currentLang].errDlGeneric);
    } finally {
        setDownloadLoading(false);
    }
});

// Download Cover Image button
downloadCoverBtn.addEventListener('click', async (e) => {
    e.stopPropagation(); // Зупиняємо розповсюдження події, щоб не вмикати аудіопрев'ю

    const src = coverImg.src;
    if (!src) return;

    const filename = `${safeFilenamePart(trackArtist.textContent || 'Artist')} - ${safeFilenamePart(trackTitle.textContent || 'Track')} (Cover)`;
    await downloadCoverImage(src, filename, downloadCoverBtn);
});

downloadCollectionCoverBtn.addEventListener('click', async () => {
    const src = currentCollection?.coverUrl;
    if (!src) return;

    const filename = `${safeFilenamePart(currentCollection.title)} (Cover)`;
    await downloadCoverImage(src, filename, downloadCollectionCoverBtn);
});

async function downloadCoverImage(src, filename, button) {
    button.disabled = true;
    try {
        const response = await fetch('/api/download-cover', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url: src, fileName: filename }),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            showError(translateError(data.error || data.detail || translations[currentLang].errCover));
            return;
        }

        const blob = await response.blob();
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = objectUrl;
        const disposition = response.headers.get('Content-Disposition') || '';
        const encodedName = disposition.match(/filename\*=UTF-8''([^;\n]*)/i);
        anchor.download = encodedName ? decodeURIComponent(encodedName[1]) : `${filename}.jpg`;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(objectUrl), 60_000);
        showDownloadNotification(anchor.download);
    } catch {
        showError(translations[currentLang].errCover);
    } finally {
        button.disabled = false;
    }
}

// Preview Player

coverOverlay.addEventListener('click', () => {
    if (!currentAudio) return;

    if (isPlaying) {
        currentAudio.pause();
        isPlaying = false;
    } else {
        currentAudio.play();
        isPlaying = true;
    }
    updatePlayIcon(isPlaying);
});

function updatePlayIcon(playing) {
    if (playing) {
        resultCover.classList.add('playing');
        coverOverlay.innerHTML = `<svg width="48" height="48" viewBox="0 0 24 24" fill="white">
            <rect x="6" y="4" width="4" height="16"/>
            <rect x="14" y="4" width="4" height="16"/>
        </svg>`;
    } else {
        resultCover.classList.remove('playing');
        coverOverlay.innerHTML = `<svg width="48" height="48" viewBox="0 0 24 24" fill="white">
            <polygon points="5,3 19,12 5,21"/>
        </svg>`;
    }
}

function stopAudio() {
    if (currentAudio) {
        currentAudio.pause();
        currentAudio.currentTime = 0;
        currentAudio = null;
    }
    isPlaying = false;
    previewProgress.style.width = '0%';
    updatePlayIcon(false);
}

// Helpers

function setDownloadLoading(on) {
    downloadBtn.disabled = on;
    dlText.hidden = on;
    dlLoader.hidden = !on;
}

function setCollectionDownloadLoading(on) {
    downloadCollectionBtn.disabled = on;
    collectionDlText.hidden = on;
    collectionDlLoader.hidden = !on;
    collectionDlProgress.textContent = on ? translations[currentLang].collectionPreparing : '';
}

function setLoading(on) {
    submitBtn.disabled = on;
    btnText.hidden = on;
    btnLoader.hidden = !on;
}

function showError(message) {
    errorSection.classList.remove('warning');
    errorMessage.textContent = message;
    errorSection.hidden = false;
}

function showWarning(message) {
    errorSection.classList.add('warning');
    errorMessage.textContent = message;
    errorSection.hidden = false;
}

function showDownloadNotification(filename) {
    window.clearTimeout(downloadNotificationTimeout);
    window.clearTimeout(downloadNotificationHideTimeout);
    downloadNotification.classList.remove('is-hiding');
    downloadNotificationMessage.textContent = translations[currentLang].downloadStarted.replace('{filename}', filename);
    downloadNotification.hidden = false;
    downloadNotificationTimeout = window.setTimeout(() => {
        downloadNotification.classList.add('is-hiding');
        downloadNotificationHideTimeout = window.setTimeout(() => {
            downloadNotification.hidden = true;
            downloadNotification.classList.remove('is-hiding');
        }, 180);
    }, 4500);
}

function hideError() {
    errorSection.classList.remove('warning');
    errorSection.hidden = true;
}

function showResult(track) {
    collectionSection.hidden = true;
    currentCollection = null;
    coverImg.src = track.coverUrl || '';
    coverImg.alt = `${track.album} — cover`;
    trackTitle.textContent = track.title;
    trackArtist.textContent = track.artist;
    trackAlbum.textContent = `${track.album}`;
    trackDuration.textContent = `⏱ ${formatDuration(track.durationMs)}`;
    trackLink.href = track.spotifyUrl;

    if (track.previewUrl) {
        currentAudio = new Audio(track.previewUrl);
        currentAudio.onended = () => stopAudio();
        currentAudio.ontimeupdate = () => {
            if (currentAudio && currentAudio.duration) {
                const percent = (currentAudio.currentTime / currentAudio.duration) * 100;
                previewProgress.style.width = `${percent}%`;
            }
        };
        coverOverlay.hidden = false;
    } else {
        coverOverlay.hidden = true;
    }

    resultSection.hidden = false;

    showArtistInfo(track.artistInfo);
}

function showCollection(collection) {
    currentCollection = collection;
    collectionTitle.textContent = collection.title;
    collectionType.textContent = translations[currentLang][collection.type] || collection.type;
    collectionCover.src = collection.coverUrl || '';
    collectionCover.hidden = !collection.coverUrl;
    collectionCoverWrap.hidden = !collection.coverUrl;
    collectionCover.alt = `${collection.title} cover`;
    collectionTrackCount.textContent = `${collection.tracks.length} ${translations[currentLang].tracks}`;
    collectionSpotifyLink.href = collection.spotifyUrl || '#';
    collectionTrackList.replaceChildren();

    collection.tracks.forEach((track, index) => {
        const row = document.createElement('li');
        const number = document.createElement('span');
        number.className = 'collection-track-number';
        number.textContent = `${index + 1}.`;
        const details = document.createElement('div');
        details.className = 'collection-track-details';
        const title = document.createElement('span');
        title.className = 'collection-track-title';
        title.textContent = track.title;
        const artist = document.createElement('span');
        artist.className = 'collection-track-artist';
        artist.textContent = track.artist;
        details.append(title, artist);
        row.append(number, details);

        const actions = document.createElement('div');
        actions.className = 'collection-track-actions';
        if (track.durationMs) {
            const duration = document.createElement('span');
            duration.className = 'collection-track-duration';
            duration.textContent = formatDuration(track.durationMs);
            actions.append(duration);
        }

        const downloadButton = document.createElement('button');
        downloadButton.type = 'button';
        downloadButton.className = 'collection-track-download';
        downloadButton.dataset.i18nTitle = 'downloadTrack';
        downloadButton.dataset.trackTitle = track.title;
        downloadButton.title = translations[currentLang].downloadTrack;
        downloadButton.setAttribute('aria-label', `${translations[currentLang].downloadTrack}: ${track.title}`);
        downloadButton.disabled = !track.spotifyUrl;
        downloadButton.innerHTML = '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';
        downloadButton.addEventListener('click', () => downloadCollectionTrack(track, downloadButton));
        actions.append(downloadButton);
        row.append(actions);
        collectionTrackList.append(row);
    });

    collectionSection.hidden = false;
    if (collection.type?.toLowerCase() === 'playlist') {
        showPlaylistCreators(collection.creatorInfos, collection.spotifyUrl);
    } else {
        showArtistInfo(collection.artistInfo);
    }
}

function refreshDynamicTranslations() {
    if (!currentCollection) return;

    collectionType.textContent = translations[currentLang][currentCollection.type] || currentCollection.type;
    collectionTrackCount.textContent = `${currentCollection.tracks.length} ${translations[currentLang].tracks}`;
    if (currentCollection.type?.toLowerCase() === 'playlist') {
        showPlaylistCreators(currentCollection.creatorInfos, currentCollection.spotifyUrl);
    }
    collectionTrackList.querySelectorAll('.collection-track-download').forEach(button => {
        button.setAttribute('aria-label', `${translations[currentLang].downloadTrack}: ${button.dataset.trackTitle}`);
    });
}

function safeFilenamePart(value) {
    return (value || '').replace(/[<>:"/\\|?*\u0000-\u001F]/g, '_').trim() || 'Unknown';
}

async function downloadCollectionTrack(track, button) {
    button.disabled = true;
    button.classList.add('is-loading');
    button.innerHTML = '<svg class="spinner" width="16" height="16" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="10" stroke="currentColor" stroke-width="3" fill="none" stroke-dasharray="30 70" stroke-linecap="round"/></svg>';
    hideError();

    try {
        const response = await fetch('/api/download', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ url: track.spotifyUrl }),
        });

        if (!response.ok) {
            const data = await response.json().catch(() => ({}));
            showError(translateError(data.error || data.detail || translations[currentLang].errDl));
            return;
        }

        const blob = await response.blob();
        const filename = `${safeFilenamePart(track.artist)} - ${safeFilenamePart(track.title)}.mp3`;
        const objectUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = objectUrl;
        anchor.download = filename;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(objectUrl), 60_000);
        showDownloadNotification(filename);
    } catch {
        showError(translations[currentLang].errDlGeneric);
    } finally {
        button.disabled = false;
        button.classList.remove('is-loading');
        button.innerHTML = '<svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';
    }
}

function showArtistInfo(artistInfo) {
    if (!artistInfo) {
        artistSection.hidden = true;
        return;
    }

    setArtistAvatar(artistInfo.avatarUrl, artistInfo.name);
    artistName.textContent = artistInfo.name;
    artistDescription.textContent = artistInfo.description || '';
    artistLink.href = artistInfo.spotifyUrl || '#';
    artistLink.hidden = !artistInfo.spotifyUrl;
    artistLinkLabel.textContent = translations[currentLang].artistProfile;
    artistSection.hidden = false;
}

function showPlaylistCreators(creators, playlistUrl) {
    if (!creators?.length) {
        artistSection.hidden = true;
        return;
    }

    const primaryCreator = creators[0];
    setArtistAvatar(primaryCreator.avatarUrl, primaryCreator.name);
    artistName.textContent = creators.map(creator => creator.name).join(', ');
    artistDescription.textContent = '';
    artistLink.href = creators.length === 1 && primaryCreator.spotifyUrl ? primaryCreator.spotifyUrl : playlistUrl || '#';
    artistLink.hidden = creators.length > 1 || !primaryCreator.spotifyUrl;
    artistLinkLabel.textContent = translations[currentLang][creators.length > 1 ? 'playlistCreators' : 'playlistCreator'];
    artistSection.hidden = false;
}

function setArtistAvatar(avatarUrl, name) {
    const initial = name?.trim().charAt(0).toLocaleUpperCase() || '?';
    artistAvatarWrap.dataset.initial = initial;
    artistAvatar.alt = `${name || ''} — avatar`;
    artistAvatarWrap.hidden = false;
    artistAvatar.hidden = !avatarUrl;
    artistAvatarWrap.classList.toggle('is-placeholder', !avatarUrl);

    if (avatarUrl) {
        artistAvatar.onerror = () => {
            artistAvatar.hidden = true;
            artistAvatarWrap.classList.add('is-placeholder');
        };
        artistAvatar.onload = () => artistAvatarWrap.classList.remove('is-placeholder');
        artistAvatar.src = avatarUrl;
    } else {
        artistAvatar.removeAttribute('src');
        artistAvatar.onerror = null;
        artistAvatar.onload = null;
    }
}

function hideResult() {
    resultSection.hidden = true;
    artistSection.hidden = true;
    collectionSection.hidden = true;
    currentCollection = null;
}

function formatDuration(ms) {
    const totalSeconds = Math.floor(ms / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${minutes}:${seconds.toString().padStart(2, '0')}`;
}

// Visitor counter
(async () => {
    try {
        // Отримуємо або генеруємо унікальний ID для цього браузера
        let vid = localStorage.getItem('spotget_vid');
        if (!vid) {
            // Генеруємо випадковий ID (fallback для старих браузерів, якщо crypto.randomUUID недоступний)
            vid = window.crypto && crypto.randomUUID ? crypto.randomUUID() : 'vid_' + Date.now().toString(36) + Math.random().toString(36).substring(2);
            localStorage.setItem('spotget_vid', vid);
        }

        const res = await fetch(`/api/visitors?vid=${vid}&_=${Date.now()}`);
        const data = await res.json();
        const el = document.getElementById('visitorCount');
        if (el && data.count !== undefined) {
            el.textContent = data.count.toLocaleString();
            el.classList.add('animate');
        }
    } catch {
        // Silently ignore — badge will stay with "—"
    }
})();
