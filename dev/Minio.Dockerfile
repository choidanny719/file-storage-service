FROM golang:1.25 AS build
RUN CGO_ENABLED=0 go install github.com/minio/minio@RELEASE.2025-10-15T17-29-55Z
RUN cp /go/pkg/mod/github.com/minio/minio@*/LICENSE /LICENSE

FROM alpine:3.22
RUN apk add --no-cache ca-certificates curl && mkdir /data && chown 10001:10001 /data
COPY --from=build /go/bin/minio /usr/local/bin/minio
COPY --from=build /LICENSE /usr/share/licenses/minio/LICENSE
USER 10001
EXPOSE 9000
ENTRYPOINT ["minio"]
