// SpotGet Frontend

const form = document.getElementById('trackForm');
const urlInput = document.getElementById('spotifyUrl');
const pasteBtn = document.getElementById('pasteBtn');
const submitBtn = document.getElementById('submitBtn');
const btnText = submitBtn.querySelector('.btn-text');
const btnLoader = submitBtn.querySelector('.btn-loader');

const errorSection = document.getElementById('errorSection');
const errorMessage = document.getElementById('errorMessage');

const resultSection = document.getElementById('resultSection');
const collectionSection = document.getElementById('collectionSection');
const collectionTitle = document.getElementById('collectionTitle');
const collectionType = document.getElementById('collectionType');
const collectionCover = document.getElementById('collectionCover');
const collectionTrackCount = document.getElementById('collectionTrackCount');
const collectionTrackList = document.getElementById('collectionTrackList');
const collectionSpotifyLink = document.getElementById('collectionSpotifyLink');
const downloadCollectionBtn = document.getElementById('downloadCollectionBtn');
const collectionDlText = downloadCollectionBtn.querySelector('.collection-dl-text');
const collectionDlLoader = downloadCollectionBtn.querySelector('.collection-dl-loader');
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
const artistAvatar      = document.getElementById('artistAvatar');
const artistName        = document.getElementById('artistName');
const artistDescription = document.getElementById('artistDescription');
const artistLink        = document.getElementById('artistLink');

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
        const response = await fetch('/api/download-collection', {
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
        const disposition = response.headers.get('Content-Disposition') || '';
        const encodedName = disposition.match(/filename\*=UTF-8''([^;\n]*)/i);
        const filename = encodedName ? decodeURIComponent(encodedName[1]) : `${collectionTitle.textContent || 'Spotify collection'}.zip`;
        const blobUrl = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = blobUrl;
        anchor.download = filename;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        URL.revokeObjectURL(blobUrl);

        const skippedTracks = Number(response.headers.get('X-SpotGet-Skipped-Tracks') || 0);
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

    try {
        const response = await fetch(src);
        const blob = await response.blob();
        const blobUrl = URL.createObjectURL(blob);

        const fileName = `${trackArtist.textContent || 'Artist'} - ${trackTitle.textContent || 'Track'} (Cover).jpg`;

        const a = document.createElement('a');
        a.style.display = 'none';
        a.href = blobUrl;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        URL.revokeObjectURL(blobUrl);
    } catch {
        // Фолбек: якщо CORS блокує пряме завантаження через blob, відкриваємо картинку в новій вкладці
        window.open(src, '_blank');
    }
});

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
        if (track.durationMs) {
            const duration = document.createElement('span');
            duration.className = 'collection-track-duration';
            duration.textContent = formatDuration(track.durationMs);
            row.append(duration);
        }
        collectionTrackList.append(row);
    });

    collectionSection.hidden = false;
    showArtistInfo(collection.artistInfo);
}

function refreshDynamicTranslations() {
    if (!currentCollection) return;

    collectionType.textContent = translations[currentLang][currentCollection.type] || currentCollection.type;
    collectionTrackCount.textContent = `${currentCollection.tracks.length} ${translations[currentLang].tracks}`;
}

function showArtistInfo(artistInfo) {
    if (!artistInfo) {
        artistSection.hidden = true;
        return;
    }

    artistAvatar.src = artistInfo.avatarUrl || '';
    artistAvatar.alt = `${artistInfo.name} — avatar`;
    artistName.textContent = artistInfo.name;
    artistDescription.textContent = artistInfo.description || '';
    artistLink.href = artistInfo.spotifyUrl || '#';
    artistSection.hidden = false;
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
