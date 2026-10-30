// Reads GET /events with the browser's own EventSource and tells .NET each kind of change as it arrives.
// EventSource reconnects by itself after a dropped connection, but gives up on a refusal, such as before signing in:
// then this tries again, more slowly each time, or at once when asked.
const kinds = ['requests', 'listen', 'library', 'tasks'];

export function listen(target) {
    let source = null;
    let timer = null;
    let delay = 1000;
    let stopped = false;

    function connect() {
        clearTimeout(timer);
        source?.close();
        source = new EventSource('api/v1/events');
        source.onopen = () => {
            delay = 1000;
            target.invokeMethodAsync('Opened');
        };
        source.onerror = () => {
            target.invokeMethodAsync('Closed');
            if (source.readyState !== EventSource.CLOSED || stopped)
                return;
            timer = setTimeout(connect, delay);
            delay = Math.min(delay * 2, 30000);
        };
        for (const kind of kinds)
            source.addEventListener(kind, () => target.invokeMethodAsync('Changed', kind));
    }

    connect();
    return {
        reconnect: () => {
            if (source?.readyState !== EventSource.OPEN) {
                delay = 1000;
                connect();
            }
        },
        dispose: () => {
            stopped = true;
            clearTimeout(timer);
            source?.close();
        },
    };
}
