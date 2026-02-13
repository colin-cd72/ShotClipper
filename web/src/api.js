const express = require('express');
const { getConfig } = require('./db');

/**
 * Creates proxy routes that forward requests to the desktop app's REST API.
 * Desktop URL and API key are resolved dynamically from the config DB on each request.
 */
function createApiProxy() {
    const router = express.Router();

    function getDesktopUrl() {
        return getConfig('desktop_api_url', process.env.SCREENER_API_URL || 'http://localhost:8080');
    }

    function getApiKey() {
        return getConfig('desktop_api_key', process.env.SCREENER_API_KEY || '');
    }

    // Generic proxy handler
    async function proxyRequest(req, res) {
        const screenerApiUrl = getDesktopUrl();
        const apiKey = getApiKey();
        const targetUrl = `${screenerApiUrl}${req.originalUrl}`;

        try {
            const headers = {
                'Content-Type': 'application/json',
                'X-API-Key': apiKey
            };

            const fetchOptions = {
                method: req.method,
                headers,
                signal: AbortSignal.timeout(10000) // 10s timeout
            };

            if (['POST', 'PUT', 'PATCH'].includes(req.method) && req.body) {
                fetchOptions.body = JSON.stringify(req.body);
            }

            const response = await fetch(targetUrl, fetchOptions);
            const contentType = response.headers.get('content-type') || '';

            if (contentType.includes('application/json')) {
                const data = await response.json();
                res.status(response.status).json(data);
            } else if (contentType.includes('video/') || contentType.includes('application/octet-stream')) {
                res.status(response.status);
                res.set('Content-Type', contentType);
                const disposition = response.headers.get('content-disposition');
                if (disposition) res.set('Content-Disposition', disposition);

                const buffer = await response.arrayBuffer();
                res.send(Buffer.from(buffer));
            } else {
                const text = await response.text();
                res.status(response.status).send(text);
            }
        } catch (err) {
            console.error(`Proxy error: ${req.method} ${targetUrl}`, err.message);
            res.status(502).json({
                error: 'Desktop app unreachable',
                details: err.message,
                desktopUrl: screenerApiUrl
            });
        }
    }

    // Proxy all API routes
    router.all('/status', proxyRequest);
    router.all('/golfers', proxyRequest);
    router.all('/golfers/import', proxyRequest);
    router.all('/golfers/:id', proxyRequest);
    router.all('/lowerthird', proxyRequest);
    router.all('/stream', proxyRequest);
    router.all('/recording', proxyRequest);
    router.all('/overlays', proxyRequest);
    router.all('/overlays/:type', proxyRequest);
    router.all('/overlays/logo', proxyRequest);
    router.all('/clips', proxyRequest);
    router.all('/clips/:name/download', proxyRequest);
    router.all('/schedules', proxyRequest);
    router.all('/schedules/:id', proxyRequest);
    router.all('/settings/:key', proxyRequest);

    return router;
}

module.exports = { createApiProxy };
