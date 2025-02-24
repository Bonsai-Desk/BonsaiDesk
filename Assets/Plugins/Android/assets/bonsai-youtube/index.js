function injectScript (func) {
    let script = document.createElement ('script');
    let code = '(' + func + ')();';
    script.textContent = code;
    (document.head || document.documentElement).appendChild (script);
    script.remove ();
}

injectScript (() => {
    let lastTag;

    function postJson (json) {
        if (window.vuplex) {
            console.log ('post json ' + JSON.stringify (json));
            window.vuplex.postMessage (json);
        } else {
            console.log ('(no vuplex) json ' + JSON.stringify (json));
        }
    }

    function bounceFromVideo () {
        // todo make sure this does not start an infinute loop
        let params = (new URL (window.location)).searchParams;
        let v = params.get ('v');
        if (v) {
            console.log ('nav to home');
            window.location = 'https://m.youtube.com';
        }

    }

    function setupScrollDetector () {
        console.log ('setup scroll detector');

        let handler;

        window.addEventListener ('scroll', e => {
            clearTimeout (handler);
            handler = setTimeout (() => {
                postJson ({Type: 'event', Message: 'stoppedScrolling'});
            }, 66);
        }, true);
    }

    function stripLinks () {
        let tag = document.activeElement.tagName;

        if (tag === 'INPUT' && tag !== lastTag) {
            postJson ({Type: 'event', Message: 'focusInput'});
        }

        if (lastTag === 'INPUT' && tag !== lastTag) {
            postJson ({Type: 'event', Message: 'blurInput'});
        }

        lastTag = tag;

        // todo make sure this only runs on YouTube
        let links = document.getElementsByTagName ('a');
        for (let i = 0; i < links.length; i++) {
            let link = links[i];
            if (link.pathname === '/watch') {
                const params = new URLSearchParams (link.search);
                const id = params.get ('v');
                if (id) {
                    link.onclick = () => {
                        postJson ({Type: 'command', Message: 'spawnYT', Data: id});
                    };
                }
                link.removeAttribute ('href');
            }
        }
    }

    stripLinks ();
    setInterval (stripLinks, 100);
    setInterval (bounceFromVideo, 100);
    setupScrollDetector ();
});


