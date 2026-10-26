FROM python:3.13-slim

WORKDIR /worker
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
    && mkdir -p /staging && chmod 1777 /staging
COPY app app

ENV STAGING_ROOT=/staging LIBRARY_ROOT=/music
VOLUME ["/staging", "/music"]
EXPOSE 8080
# Run as the user that owns the music library, so files it writes belong to them.
USER 1000:1000
CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
