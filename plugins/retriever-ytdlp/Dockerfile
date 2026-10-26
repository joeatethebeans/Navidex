FROM python:3.13-slim

# ffmpeg remuxes YouTube's audio into its own file; deno solves YouTube's JavaScript challenges for yt-dlp.
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg ca-certificates && rm -rf /var/lib/apt/lists/*
COPY --from=docker.io/denoland/deno:bin-2.5.6 /deno /usr/local/bin/deno

WORKDIR /plugin
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    && mkdir -p /staging && chmod 1777 /staging
COPY app app

ENV STAGING_ROOT=/staging PARALLEL_FETCHES=2
VOLUME /staging
EXPOSE 8080
USER 1000:1000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
