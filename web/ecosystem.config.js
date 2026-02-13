module.exports = {
    apps: [{
        name: 'screener-panel',
        script: 'server.js',
        cwd: '/home/shotclipper/htdocs/shotclipper.4tmrw.net',
        instances: 1,
        env: {
            NODE_ENV: 'production',
            PORT: 5010
        }
    }]
};
