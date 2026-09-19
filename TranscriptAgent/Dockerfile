FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV PIP_DISABLE_PIP_VERSION_CHECK=1
ENV YATING_PIPELINE=asr-zh-en-std

WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY src ./src
COPY knowledge ./knowledge

RUN adduser --disabled-password --gecos "" --home /app appuser \
    && chown -R appuser:appuser /app
USER appuser

CMD ["python", "src/agent.py", "start"]
