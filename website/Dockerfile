# The website as a container image, served by unprivileged nginx on port 8080.
# Built and pushed to ghcr.io/synexyconsulting/touchdeck-website by .github/workflows/website.yml.
# The stack that runs it behind Traefik lives in the private SynexyConsulting/websites repo.
#
#   docker build -t touchdeck-website website && docker run --rm -p 8080:8080 touchdeck-website
FROM nginxinc/nginx-unprivileged:1.31.6-alpine

COPY nginx.conf /etc/nginx/conf.d/default.conf
# Only what the page uses: README.md and tools/ stay out of the image.
COPY index.html license.html /usr/share/nginx/html/
COPY assets /usr/share/nginx/html/assets

EXPOSE 8080
